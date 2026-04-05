// SPDX-License-Identifier: MIT
// SemanticControllerEditor.cs - Custom Inspector for SemanticController

using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEngine;

namespace GaussianSplatting.Editor
{
    [CustomEditor(typeof(SemanticController))]
    public class SemanticControllerEditor : UnityEditor.Editor
    {
        SerializedProperty m_PropSemanticNames;
        SerializedProperty m_PropSemanticDictAsset;

        void OnEnable()
        {
            m_PropSemanticNames = serializedObject.FindProperty("m_SemanticNames");
            m_PropSemanticDictAsset = serializedObject.FindProperty("m_SemanticDictAsset");
        }

        public override void OnInspectorGUI()
        {
            var sc = (SemanticController)target;
            serializedObject.Update();

            // -- Semantic Dictionary JSON import --
            EditorGUILayout.LabelField("Semantic Dictionary", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(m_PropSemanticDictAsset,
                new GUIContent("Dict JSON", "Drag semantic_dict.json here"));
            if (EditorGUI.EndChangeCheck())
            {
                serializedObject.ApplyModifiedProperties();
            }

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Load from JSON"))
            {
                if (sc.semanticDictAsset != null)
                {
                    Undo.RecordObject(sc, "Load Semantic Dict");
                    // Ensure initialized from asset first (may not have run yet)
                    if (sc.activeLayerCount == 0)
                    {
                        var renderer = sc.GetComponent<GaussianSplatRenderer>();
                        if (renderer != null && renderer.HasValidAsset)
                            sc.InitializeFromAsset(renderer.asset);
                    }
                    sc.LoadFromAssignedAsset();
                    EditorUtility.SetDirty(sc);
                    serializedObject.Update();
                }
                else
                {
                    EditorUtility.DisplayDialog("No JSON Assigned",
                        "Please drag semantic_dict.json into the Dict JSON field first.", "OK");
                }
            }

            if (GUILayout.Button("Reinitialize"))
            {
                var renderer = sc.GetComponent<GaussianSplatRenderer>();
                if (renderer != null)
                {
                    renderer.enabled = false;
                    renderer.enabled = true;
                    EditorUtility.SetDirty(sc);
                    serializedObject.Update();
                    Debug.Log("[SemanticController] Reinitialized from Renderer asset.");
                }
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(4);

            // -- Semantic Names (active layers only) --
            EditorGUILayout.LabelField("Semantic Names", EditorStyles.boldLabel);

            var activeIds = sc.activeLayerIds;
            if (activeIds != null && activeIds.Length > 0)
            {
                EditorGUI.indentLevel++;
                for (int idx = 0; idx < activeIds.Length; idx++)
                {
                    int layerId = activeIds[idx];
                    int splatCount = sc.GetLayerSplatCount(layerId);
                    string currentName = sc.GetSemanticName(layerId);

                    EditorGUI.BeginChangeCheck();
                    string newName = EditorGUILayout.TextField(
                        $"Layer {layerId} ({splatCount:N0})", currentName);
                    if (EditorGUI.EndChangeCheck())
                    {
                        Undo.RecordObject(sc, "Edit Semantic Name");
                        sc.SetSemanticName(layerId, newName);
                        EditorUtility.SetDirty(sc);
                    }
                }
                EditorGUI.indentLevel--;
            }
            else
            {
                EditorGUILayout.HelpBox(
                    "Semantic names list is empty.\n" +
                    "1. Drag semantic_dict.json into the Dict JSON field above\n" +
                    "2. Click \"Load from JSON\" to import\n" +
                    "3. Or click \"Reinitialize\" to reload from the Asset",
                    MessageType.Info);
            }

            // -- Status --
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Status", EditorStyles.boldLabel);
            EditorGUI.BeginDisabledGroup(true);
            EditorGUILayout.IntField("Active Layers", sc.activeLayerCount);
            EditorGUILayout.IntField("GPU Buffer Size (maxId+1)", sc.semanticCount);
            EditorGUILayout.Toggle("GPU Visibility Ready", sc.gpuVisibility != null);
            EditorGUILayout.Toggle("GPU Tints Ready", sc.gpuTints != null);
            EditorGUI.EndDisabledGroup();

            serializedObject.ApplyModifiedProperties();
        }
    }
}