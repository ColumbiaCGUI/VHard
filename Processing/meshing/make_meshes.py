# -*- coding: utf-8 -*-
"""
Created on Mon Jul  8 22:22:46 2024

@author: JOEL
"""

import open3d as o3d
from open3d.utility import VerbosityContextManager, VerbosityLevel

import os
import numpy as np
from io_utils import load_ply

def estimate_normals(files):
    
    clouds = dict()
    for file in files:
        
        # Load points
        points, colors = load_ply(file)
        pcd = o3d.geometry.PointCloud()
        pcd.points = o3d.utility.Vector3dVector(points)
        if colors is not None:
            pcd.colors = o3d.utility.Vector3dVector(colors)
            
        # Estimate normals
        pcd.estimate_normals(
            search_param=o3d.geometry.KDTreeSearchParamHybrid(
            radius=0.1, max_nn=30)
        )
        if not pcd.has_normals():
            print(f"Warning: Normal estimation failed for {file}")
            continue
        pcd.orient_normals_consistent_tangent_plane(100)
        
        # Remove outliers
        cl, ind = pcd.remove_statistical_outlier(nb_neighbors=20, std_ratio=2.0)
        pcd = pcd.select_by_index(ind)
        
        # Save to dict
        clouds[file] = pcd
        
    return clouds


def poisson(clouds):
    
    for file, pcd in clouds.items():
        
        # Construct mesh
        with VerbosityContextManager(VerbosityLevel.Debug) as cm:
            
            # Apply Poisson reconstruction
            mesh, densities = \
                o3d.geometry.TriangleMesh.create_from_point_cloud_poisson(
                pcd, depth=9
            )
                
            # Remove low density vertices
            vertices_to_remove = densities < np.quantile(densities, 0.1)
            mesh.remove_vertices_by_mask(vertices_to_remove)
            
            # Smooth the mesh
            mesh = mesh.filter_smooth_taubin(number_of_iterations=100)
            
            # Write to file
            filename = file.replace('holds', 'poisson')
            o3d.io.write_triangle_mesh(filename, mesh)
            
        print(f'Poissoned {os.path.basename(file[:-4])}')
        
        
def ball_pivoting(clouds):
    
    for file, pcd in clouds.items():
        
        # Compute parameters
        distances = pcd.compute_nearest_neighbor_distance()
        avg_dist = np.mean(distances)
        radii = [avg_dist * (2**n) for n in range(1, 5)]
        
        with VerbosityContextManager(VerbosityLevel.Debug) as cm:
            
            # Apply Ball Pivoting
            mesh = \
                o3d.geometry.TriangleMesh.create_from_point_cloud_ball_pivoting(
                pcd, o3d.utility.DoubleVector(radii)
            )
            
            # Use os.path.basename to get just the filename, not the full path
            filename = file.replace('holds', 'ball_pivoting')
            o3d.io.write_triangle_mesh(filename, mesh)
        
        print(f'Ball Pivoting applied to {os.path.basename(file[:-4])}')
        

if __name__ == '__main__':
    
    # files = os.listdir('holds')
    holds = ['A5', 'D5', 'G8', 'G10', 'H11', 'J13', 'F18', 'H15', 'C13', 
             'F12', 'B8', 'E4', 'I2', 'F13', 'E11', 'D9', 'G36']
    files = [os.path.join('holds', f'{h}.ply') for h in holds]
    
    clouds = estimate_normals(files)
    
    poisson(clouds)
    # ball_pivoting(clouds)
    
    