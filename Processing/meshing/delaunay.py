import bpy
import bmesh
import numpy as np
from mathutils import Vector
import site
import sys
user_site = site.getusersitepackages()
if user_site not in sys.path:
    sys.path.append(user_site)
import scipy
from scipy.spatial import Delaunay

def triangulate_point_cloud(obj_name="Point_Cloud"):
    """
    Triangulates points in a point cloud object and creates a mesh while preserving vertex colors.
    
    Args:
        obj_name (str): Name of the point cloud object in the scene
    """
    # Get the point cloud object
    if obj_name not in bpy.data.objects:
        raise ValueError(f"Object {obj_name} not found in scene")
    
    point_cloud = bpy.data.objects[obj_name]
    mesh_data = point_cloud.data
    
    # Check if vertex colors exist
    if not mesh_data.vertex_colors:
        print("Warning: No vertex colors found in the source mesh")
        return None
    
    color_layer = mesh_data.vertex_colors.active
    
    # Create a bmesh from the original mesh to easily access vertex colors
    bm = bmesh.new()
    bm.from_mesh(mesh_data)
    
    # Ensure we have vertex color data
    color_layer_bm = bm.loops.layers.color.verify()
    
    # Store vertex colors keyed by vertex index
    vertex_colors = {}
    for face in bm.faces:
        for loop in face.loops:
            vertex_colors[loop.vert.index] = loop[color_layer_bm]
    
    # Extract points from the object
    points = [vertex.co.xyz for vertex in mesh_data.vertices]
    points_2d = np.array([[p[0], p[1]] for p in points])  # Project to 2D for triangulation
    
    # Perform Delaunay triangulation
    tri = Delaunay(points_2d)
    
    # Create new mesh
    new_mesh = bpy.data.meshes.new(name=f"{obj_name}_Triangulated")
    
    # Create bmesh for new mesh
    bm_new = bmesh.new()
    
    # Create vertices in bmesh
    new_verts = [bm_new.verts.new(point) for point in points]
    bm_new.verts.ensure_lookup_table()
    
    # Create faces in bmesh
    for simplex in tri.simplices:
        bm_new.faces.new([new_verts[i] for i in simplex])
    
    # Create color layer in new bmesh
    new_color_layer = bm_new.loops.layers.color.new("Col")
    
    # Transfer colors
    for face in bm_new.faces:
        for loop in face.loops:
            if loop.vert.index in vertex_colors:
                loop[new_color_layer] = vertex_colors[loop.vert.index]
    
    # Update mesh
    bm_new.to_mesh(new_mesh)
    new_mesh.update()
    
    # Create new object
    new_obj = bpy.data.objects.new(f"{obj_name}_Triangulated", new_mesh)
    bpy.context.collection.objects.link(new_obj)
    
    # Clean up
    bm.free()
    bm_new.free()
    
    # Position the new object at the same location as the original
    new_obj.matrix_world = point_cloud.matrix_world.copy()
    
    return new_obj

def main():
    # Example usage
    try:
        triangulated = triangulate_point_cloud("lowdown_unsegmented")
        print("Triangulation completed successfully!")
    except Exception as e:
        print(f"Error during triangulation: {str(e)}")

if __name__ == "__main__":
    main()