import os

from segment_holds import segment
from make_meshes import clip_multiple_point_clouds, estimate_normals, poisson

if __name__ == '__main__':


    unsegmented = 'cleaned_unsegmented.ply'
    lattice = 'lattice.ply'
    segment(unsegmented, lattice)

    files = [os.path.join('holds', h) for h in os.listdir('holds')]
    
    clipped_clouds = clip_multiple_point_clouds(files, 'plane.ply')
    
    clouds_with_normals = estimate_normals(clipped_clouds)
    
    poisson(clouds_with_normals)