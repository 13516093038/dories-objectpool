using System;
using System.Collections.Generic;
using System.Linq;
using Dories.ObjectPool.Runtime;
using UnityEditor;
using UnityEngine;

namespace Dories.ObjectPool.Editor
{
    [CustomEditor(typeof(GameObjectPoolEntity))]
    public class GameObjectPoolEntityInspector : UnityEditor.Editor
    {
        private const string ProviderField = "_prefabProvider";

        private readonly List<(string Package, string Location)> _keys = new();
        private Type[] _providerTypes;
        private string[] _providerNames;
        private Vector2 _scrollPosition;
        private bool _poolFoldout = true;

        public override bool RequiresConstantRepaint()
        {
            return Application.isPlaying;
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            DrawPropertiesExcluding(serializedObject, ProviderField);
            DrawProviderSelector(serializedObject.FindProperty(ProviderField));
            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.Space(6);
            DrawPoolMonitor();
        }

        private void DrawProviderSelector(SerializedProperty property)
        {
            if (_providerTypes == null)
            {
                // SerializeReference 只能放可序列化的非 UnityEngine.Object 类型，且下拉创建需要无参构造
                _providerTypes = TypeCache.GetTypesDerivedFrom<IPrefabProvider>()
                    .Where(t => !t.IsAbstract && !t.IsGenericType && t.IsSerializable
                                && !typeof(UnityEngine.Object).IsAssignableFrom(t)
                                && t.GetConstructor(Type.EmptyTypes) != null)
                    .OrderBy(t => t.Name)
                    .ToArray();
                _providerNames = new[] { "(自动)" }.Concat(_providerTypes.Select(t => t.Name)).ToArray();
            }

            // managedReferenceFullTypename 格式为 "程序集名 完整类型名"
            var current = property.managedReferenceFullTypename;
            int index = Array.FindIndex(_providerTypes, t => $"{t.Assembly.GetName().Name} {t.FullName}" == current) + 1;

            using (new EditorGUI.DisabledScope(Application.isPlaying))
            {
                int selected = EditorGUILayout.Popup("Prefab Provider", index, _providerNames);
                if (selected != index)
                    property.managedReferenceValue = selected == 0 ? null : Activator.CreateInstance(_providerTypes[selected - 1]);
            }

            if (property.managedReferenceValue == null)
                return;

            EditorGUI.indentLevel++;
            var end = property.GetEndProperty();
            var child = property.Copy();
            for (bool enter = true; child.NextVisible(enter) && !SerializedProperty.EqualContents(child, end); enter = false)
                EditorGUILayout.PropertyField(child, true);
            EditorGUI.indentLevel--;
        }

        private void DrawPoolMonitor()
        {
            _poolFoldout = EditorGUILayout.Foldout(_poolFoldout, "Pools", true);
            if (!_poolFoldout)
                return;

            EditorGUI.indentLevel++;

            if (!Application.isPlaying)
            {
                EditorGUILayout.HelpBox("进入 Play Mode 后显示对象池。", MessageType.Info);
                EditorGUI.indentLevel--;
                return;
            }

            var entity = (GameObjectPoolEntity)target;
            var pools = entity.Pools;

            EditorGUILayout.LabelField("PrefabProvider", entity.PrefabProvider?.GetType().Name ?? "(未设置)");
            EditorGUILayout.LabelField("池数量", pools.Count.ToString());
            EditorGUILayout.Space(4);

            if (pools.Count == 0)
            {
                EditorGUILayout.LabelField("(无对象池)", EditorStyles.miniLabel);
                EditorGUI.indentLevel--;
                return;
            }

            DrawTableHeader();

            // 先拷贝 key，行内按钮会修改字典
            _keys.Clear();
            _keys.AddRange(pools.Keys);
            (string Package, string Location)? releaseKey = null;
            float now = Time.unscaledTime;

            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition, GUILayout.MaxHeight(280f));
            foreach (var key in _keys)
            {
                var pp = pools[key];
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label(GameObjectPoolEntity.DisplayName(key));
                GUILayout.Label(pp.ActiveCount.ToString(), GUILayout.Width(48f));
                GUILayout.Label(pp.Pool.CountInactive.ToString(), GUILayout.Width(56f));
                GUILayout.Label(pp.KeepAlive ? "Keep" : pp.ActiveCount > 0 ? "-" : $"{now - pp.LastUseTime:F0}s", GUILayout.Width(48f));
                if (GUILayout.Button("Release", EditorStyles.miniButton, GUILayout.Width(60f)))
                    releaseKey = key;
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndScrollView();

            if (releaseKey is { } k)
                entity.ReleasePool(k.Location, k.Package);

            EditorGUILayout.Space(4);
            if (GUILayout.Button("Release Unused"))
                entity.ReleaseUnused();

            EditorGUI.indentLevel--;
        }

        private static void DrawTableHeader()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            EditorGUILayout.LabelField("Key", EditorStyles.miniLabel);
            EditorGUILayout.LabelField("Active", EditorStyles.miniLabel, GUILayout.Width(48f));
            EditorGUILayout.LabelField("Inactive", EditorStyles.miniLabel, GUILayout.Width(56f));
            EditorGUILayout.LabelField("Idle", EditorStyles.miniLabel, GUILayout.Width(48f));
            GUILayout.Space(60f);
            EditorGUILayout.EndHorizontal();
        }
    }
}
