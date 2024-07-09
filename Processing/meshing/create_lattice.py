# -*- coding: utf-8 -*-
"""
Created on Mon Jul  8 11:19:32 2024

@author: JOEL
"""

import numpy as np
import bpy
import bmesh
import os

def create_lattice(p1, p2, p3, H=18, W=11):
    """
    Creates a lattice of screwholes, used for segmenting holds.

    Parameters
    ----------
    p1 : np.array
        One screwhole coordinates.
    p2 : np.array
        Another screwhole coordinates.
    p3 : np.array
        A third screwhole coordinates.
    H : int, optional
        Moonboard height. The default is 18.
    W : int, optional
        Moonboard width. The default is 11.

    Returns
    -------
    output_path : str
        Path to lattice PLY file.

    """
    
    # Calculate basis vectors
    v1 = p2 - p1
    v2 = p3 - p1
    
    # Create grid of points
    u = np.linspace(0, 1, W)
    v = np.linspace(0, 1, H)
    uu, vv = np.meshgrid(u, v)
    points = (p1 + uu[:, :, np.newaxis] * v1 + vv[:, :, np.newaxis] * v2).T
    
    # Put the points in a Blender mesh
    mesh = bpy.data.meshes.new(name="Lattice")
    obj = bpy.data.objects.new("Lattice", mesh)
    bpy.context.collection.objects.link(obj)
    bm = bmesh.new()
    for i in range(W):
        for j in range(H):
            bm.verts.new(points[:, i, j])
    bm.to_mesh(mesh)
    bm.free()
    mesh.update()
    
    # Save to PLY
    output_path = os.path.abspath('cleaned_unsegmented.ply')
    bpy.ops.object.select_all(action='DESELECT')
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    bpy.ops.export_mesh.ply(
        filepath=output_path,
        use_selection=True,
        use_mesh_modifiers=True,
        use_normals=True,
        use_uv_coords=True,
        use_colors=True
    )
    
    return output_path

if __name__ == '__main__':
    
    p1 = np.array([2.84502, -3.75095, -0.412297])
    p2 = np.array([-2.06245, -3.8427, -0.10847])
    p3 = np.array([3.20163, 1.95115, 5.98893])
    
    obj = create_lattice(p1, p2, p3)