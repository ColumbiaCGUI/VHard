import os

import open3d as o3d

from segment_holds import segment
from make_meshes import calculate_plane_params, clip_multiple_point_clouds, \
    estimate_normals, poisson, refine_meshes

if __name__ == '__main__':

    unsegmented = 'cleaned_unsegmented.ply'
    lattice = 'lattice.ply'
    segment(unsegmented, lattice)

    files = [os.path.join('holds', h) for h in os.listdir('holds')]
    
    plane_mesh = o3d.io.read_triangle_mesh('plane.ply')
    plane_point, plane_normal = calculate_plane_params(plane_mesh)
    clipped_clouds = clip_multiple_point_clouds(files, plane_point, plane_normal)
    
    clouds_with_normals = estimate_normals(clipped_clouds)
    
    poisson(clouds_with_normals)
    plane_mesh = o3d.t.io.read_triangle_mesh('plane.ply')
    refine_meshes(plane_point, plane_normal*-1 + .01)