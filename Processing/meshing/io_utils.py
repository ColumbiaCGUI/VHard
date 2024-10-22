# -*- coding: utf-8 -*-
"""
Created on Mon Jul  1 12:38:49 2024

@author: JOEL
"""
import numpy as np
import open3d as o3d
import os


def o3d_mesh_to_blender(o3d_mesh, name):
    
    import bpy
    
    # Convert Open3D mesh to Blender mesh
    vertices = np.asarray(o3d_mesh.vertices)
    triangles = np.asarray(o3d_mesh.triangles)
    
    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata(vertices, [], triangles)
    mesh.update()
    
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.collection.objects.link(obj)
    return obj


def load_ply(file_path):
    
    pcd = o3d.io.read_point_cloud(file_path)
    points = np.asarray(pcd.points)
    colors = np.asarray(pcd.colors) if 'colors' in dir(pcd) else None
    return points, colors


def write_ply(points, colors, file_path):
    
    pcd = o3d.geometry.PointCloud()
    pcd.points = o3d.utility.Vector3dVector(points)
    
    if colors is not None and len(colors):
        if np.max(colors) > 1:
            colors = colors / 255.0
        pcd.colors = o3d.utility.Vector3dVector(colors)
    
    o3d.io.write_point_cloud(file_path, pcd, write_ascii=False)

