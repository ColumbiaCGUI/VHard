using System.Linq;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

/*
 * Find the corresponding decimated holds FBX files and replace the meshes of New Holds with the ones in the decimated files, also assign materials.
 * To use, the New_Holds GameObject should be selected in the Hierarchy before running the script (and enable Debugger).
 * The decimated holds are located in Assets/Resources/Decimated_Holds, e.g. B126_dec50.fbx,
 * original holds located in Assets/Resources/New_Holds.fbx.
 */
public class ChangeNewHoldsMeshes
{
    private const string DecimatedFolder = "Assets/Resources/Decimated_Holds";
    private const string DecimatedSuffix = "_dec50";

    [MenuItem("Custom/Holds/Replace New Holds With Decimated Meshes And Materials")]
    static void ReplaceMeshesAndMaterials()
    {
        GameObject root = Selection.activeGameObject;

        if (root == null)
        {
            Debug.LogError("Please select the New_Holds GameObject in the Hierarchy first.");
            return;
        }

        MeshFilter[] meshFilters = root.GetComponentsInChildren<MeshFilter>(true);

        int replacedMeshes = 0;
        int replacedMaterials = 0;
        int missingFbx = 0;
        int missingMesh = 0;
        int missingMaterial = 0;

        foreach (MeshFilter meshFilter in meshFilters)
        {
            GameObject holdObject = meshFilter.gameObject;
            string objectName = holdObject.name.Trim(); // e.g. B126

            if (string.IsNullOrEmpty(objectName))
            {
                continue;
            }

            string expectedFbxPath = $"{DecimatedFolder}/{objectName}{DecimatedSuffix}.fbx";

            if (!System.IO.File.Exists(expectedFbxPath))
            {
                Debug.LogWarning($"Missing decimated FBX for {objectName}: {expectedFbxPath}");
                missingFbx++;
                continue;
            }

            Object[] subAssets = AssetDatabase.LoadAllAssetsAtPath(expectedFbxPath);

            Mesh decimatedMesh = subAssets
                .OfType<Mesh>()
                .FirstOrDefault();

            Material decimatedMaterial = subAssets
                .OfType<Material>()
                .FirstOrDefault();

            if (decimatedMesh == null)
            {
                Debug.LogWarning($"No Mesh found inside {expectedFbxPath}");
                missingMesh++;
                continue;
            }

            Undo.RecordObject(meshFilter, "Replace Hold Mesh");
            meshFilter.sharedMesh = decimatedMesh;
            EditorUtility.SetDirty(meshFilter);
            replacedMeshes++;

            Renderer renderer = holdObject.GetComponent<Renderer>();

            if (renderer == null)
            {
                renderer = holdObject.GetComponentInChildren<Renderer>(true);
            }

            if (renderer != null)
            {
                if (decimatedMaterial != null)
                {
                    Undo.RecordObject(renderer, "Replace Hold Material");
                    renderer.sharedMaterial = decimatedMaterial;
                    EditorUtility.SetDirty(renderer);
                    replacedMaterials++;
                }
                else
                {
                    Debug.LogWarning($"No Material found inside {expectedFbxPath}. Mesh replaced but material unchanged.");
                    missingMaterial++;
                }
            }
        }

        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());

        Debug.Log(
            $"\n===============================================\n" +
            $"Finished hold replacement under {root.name}.\n" +
            $"Meshes replaced: {replacedMeshes}\n" +
            $"Materials replaced: {replacedMaterials}\n" +
            $"Missing FBX: {missingFbx}\n" +
            $"Missing Mesh: {missingMesh}\n" +
            $"Missing Material: {missingMaterial} " +
            $"================================\n"
        );
    }
}
/* ########################################################################################## 06/12/2026
 * Version 1.0: Basic logic and pseudocode to replace meshes and materials based on filenames. 
 * find the corresponding decimated meshes and replace the meshes of New Holds with the decimated ones
 * find B/W/Y in the filename and assign materials accordingly
 * string objectname = renderer.gameObject.name;
 * char colorCode = objectname[0];
 * string holdID = objectname.Substring(1, objectname.Length - 1);
 * assets.FindAssets(colorCode + holdID + "_dec50") to find the corresponding decimated mesh
 * --> sample code
 * string[] guids = AssetDatabase.FindAssets(colorCode + holdID + "_dec50");
 * foreach (string guid in guids){
    string path = AssetDatabase.GUIDToAssetPath(guid);
    UnityEngine.Object myAsset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
    }
 *  if (myAsset != null) {
 *      // Replace the mesh of the New Hold with the decimated mesh
 *      MeshFilter meshFilter = renderer.GetComponent<MeshFilter>();
 *      if (meshFilter != null) {
 *          meshFilter.sharedMesh = myAsset as Mesh;
 *      }
 *  }
 *  --> assign material
 *  if (colorCode == 'B') {
 *  renderer.sharedMaterial = Resources.Load("Materials/Black") as Material;
 *  }
 *  if (colorCode == 'W') {
 *      renderer.sharedMaterial = Resources.Load("Materials/White") as Material;
 *  }
 *  if (colorCode == 'Y') {
 *      renderer.sharedMaterial = Resources.Load("Materials/Yellow") as Material;
 *  }
 ########################################################################################## 06/13/2026
 *  Version 2.0: Find the mesh and material inside the expected FBX file, instead of comparing names.
 *  Version 2.1: Add error handling and logging for missing FBX, missing mesh, and missing material cases.
 *  Version 2.2: Custom menu and corresponding debug logs.
 */
