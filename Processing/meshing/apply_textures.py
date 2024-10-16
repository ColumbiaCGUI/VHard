import bpy

def apply_material_to_selected(material_name):
    # Get the material
    mat = bpy.data.materials.get(material_name)
    if mat is None:
        print(f"Material {material_name} not found")
        return

    # Loop through selected objects
    for obj in bpy.context.selected_objects:
        if obj.type == 'MESH':
            # Assign material to object
            if obj.data.materials:
                obj.data.materials[0] = mat
            else:
                obj.data.materials.append(mat)

# Usage
apply_material_to_selected("VertexColors")