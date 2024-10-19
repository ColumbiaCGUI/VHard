# -*- coding: utf-8 -*-
"""
Created on Mon Jul  8 22:22:46 2024

@author: JOEL
"""

import open3d as o3d
from open3d.utility import VerbosityContextManager, VerbosityLevel
from scipy.spatial import ConvexHull

import os
import numpy as np
from io_utils import load_ply


def calculate_plane_params(plane_mesh):

    vertices = np.asarray(plane_mesh.vertices)
    centroid = np.mean(vertices, axis=0)

    # Calculate normal using just the first three vertices
    v0, v1, v2 = vertices[:3]
    normal = np.cross(v1 - v0, v2 - v0)
    normal = normal / np.linalg.norm(normal)

    # Orient the normal towards the origin
    if np.dot(centroid, normal) < 0:
        normal = -normal

    return centroid, normal


def clip_by_plane(pcd, plane_point, plane_normal, grid_resolution=0.005):

    # Clip points that are on the correct side of the plane
    points = np.asarray(pcd.points)
    colors = np.asarray(pcd.colors)
    vectors = points - plane_point
    distances = np.dot(vectors, plane_normal)
    mask = distances <= 0
    filtered_points = points[mask]
    filtered_colors = colors[mask]

    # Project points onto the plane
    projected_points = filtered_points - np.outer(distances[mask], plane_normal)

    # Find two orthogonal vectors in the plane
    u = np.cross(plane_normal, [1, 0, 0])
    if np.allclose(u, 0):
        u = np.cross(plane_normal, [0, 1, 0])
    u = u / np.linalg.norm(u)
    v = np.cross(plane_normal, u)

    # Project the points onto the 2D coordinate system defined by u and v
    points_2d = np.column_stack([np.dot(projected_points - plane_point, u),
                                 np.dot(projected_points - plane_point, v)])

    # Compute 2D convex hull
    hull = ConvexHull(points_2d)
    hull_points_2d = points_2d[hull.vertices]

    # Determine bounding box of hull points
    min_bound = np.min(hull_points_2d, axis=0)
    max_bound = np.max(hull_points_2d, axis=0)

    # Create a grid of points
    x = np.arange(min_bound[0], max_bound[0], grid_resolution)
    y = np.arange(min_bound[1], max_bound[1], grid_resolution)
    xx, yy = np.meshgrid(x, y)
    grid_points_2d = np.column_stack((xx.ravel(), yy.ravel()))

    # Check which points are inside the hull
    def in_hull(p, hull):
        return all((np.dot(eq[:-1], p) + eq[-1] <= 0) for eq in hull.equations)
    
    inside_mask = np.array([in_hull(point, hull) for point in grid_points_2d])
    
    # Convert 2D intersection points back to 3D
    intersection_points_2d = grid_points_2d[inside_mask]
    intersection_points = (plane_point.reshape(1, 3) +
                           np.outer(intersection_points_2d[:, 0], u) +
                           np.outer(intersection_points_2d[:, 1], v))

   # Combine the filtered points and intersection points
    combined_points = np.vstack((filtered_points, intersection_points))
    
    # Create new colors for intersection points (e.g., white)
    intersection_colors = np.ones((len(intersection_points), 3))
    combined_colors = np.vstack((filtered_colors, intersection_colors))

    # Create and return the new point cloud
    new_pcd = o3d.geometry.PointCloud()
    new_pcd.points = o3d.utility.Vector3dVector(combined_points)
    new_pcd.colors = o3d.utility.Vector3dVector(combined_colors)

    return new_pcd


def clip_multiple_point_clouds(files, plane_point, plane_normal):

    clipped_clouds = {}
    for file in files:
        try:
            points, colors = load_ply(file)
            pcd = o3d.geometry.PointCloud()
            pcd.points = o3d.utility.Vector3dVector(points)
            if colors is not None:
                pcd.colors = o3d.utility.Vector3dVector(colors)
            
            clipped_pcd = clip_by_plane(pcd, plane_point, plane_normal)
            clipped_clouds[file] = clipped_pcd
            
            print(f'Clipped {os.path.basename(file[:-4])}')

        except Exception as e:
            print(f'FAILED TO CLIP {file}: {e}')

    return clipped_clouds


def estimate_normals(clouds):

    for file, pcd in clouds.items():
        try:
            pcd.estimate_normals(
                search_param=o3d.geometry.KDTreeSearchParamHybrid(
                radius=0.1, max_nn=30)
            )
            if not pcd.has_normals():
                print(f"Warning: Normal estimation failed for {file}")
                continue
            pcd.orient_normals_consistent_tangent_plane(100)
            
            cl, ind = pcd.remove_statistical_outlier(
                nb_neighbors=20, 
                std_ratio=2.0
            )
            clouds[file] = pcd.select_by_index(ind)
            
        except Exception as e:
            print(f'FAILED TO ESTIMATE NORMALS FOR {file}: {e}')
    
    return clouds

def poisson(clouds, depth=6):

    for file, pcd in clouds.items():
        try:
            with VerbosityContextManager(VerbosityLevel.Debug) as cm:

                # Poisson surface reconstruction
                mesh, densities = o3d.geometry.TriangleMesh.create_from_point_cloud_poisson(
                    pcd, depth=depth
                )
                
                # Remove useless vertices
                vertices_to_remove = densities < np.quantile(densities, 0.01)
                mesh.remove_vertices_by_mask(vertices_to_remove)
                
                # Smoothing
                mesh = mesh.filter_smooth_taubin(number_of_iterations=100)
                
                # Check if the point cloud has colors
                if not pcd.has_colors():
                    print(f"Warning: Point cloud for {file} does not have colors. Skipping color transfer.")
                    continue
                
                # Transfer colors from point cloud to mesh using nearest neighbor search
                pcd_tree = o3d.geometry.KDTreeFlann(pcd)
                mesh_vertices = np.asarray(mesh.vertices)
                pcd_colors = np.asarray(pcd.colors)
                vertex_colors = []
                
                for vertex in mesh_vertices:
                    _, idx, _ = pcd_tree.search_knn_vector_3d(vertex, 1)
                    if len(idx) > 0:
                        closest_point_color = pcd_colors[idx[0]]
                    else:
                        # If no nearest neighbor is found, use a default color (e.g., white)
                        closest_point_color = np.array([1.0, 1.0, 1.0])
                    vertex_colors.append(closest_point_color)
                
                mesh.vertex_colors = o3d.utility.Vector3dVector(np.array(vertex_colors))
                
                # Write file
                filename = file.replace('holds', 'poisson')
                o3d.io.write_triangle_mesh(filename, mesh)
                
            print(f'Poissoned {os.path.basename(file[:-4])}')

        except Exception as e:
            print(f"Error processing {file}: {str(e)}")


def refine_meshes(plane_point, plane_normal, in_folder='poisson', out_folder='refined'):

    for file in os.listdir(in_folder):

        # Read mesh
        filename = os.path.join(in_folder, file)
        mesh = o3d.t.io.read_triangle_mesh(filename, enable_post_processing=True)

        # Fill holes
        mesh = mesh.fill_holes(hole_size=.1)

        # Clip plane
        mesh = mesh.clip_plane(plane_point, plane_normal)
        convex_hull = mesh.compute_convex_hull()
        intersection = convex_hull.boolean_intersection(plane_mesh)
        mesh  = mesh.boolean_union(intersection)

        # Write new mesh
        outfile = os.path.join(out_folder, file)
        o3d.t.io.write_triangle_mesh(outfile, mesh)


if __name__ == '__main__':

    files = [os.path.join('holds', h) for h in os.listdir('holds')]
    
    plane_mesh = o3d.io.read_triangle_mesh('plane.ply')
    plane_point, plane_normal = calculate_plane_params(plane_mesh)
    # clipped_clouds = clip_multiple_point_clouds(files, plane_point, plane_normal)
    
    # clouds_with_normals = estimate_normals(clipped_clouds)
    
    # poisson(clouds_with_normals)
    plane_mesh = o3d.t.io.read_triangle_mesh('plane.ply')
    refine_meshes(plane_point, plane_normal*-1 + .01)