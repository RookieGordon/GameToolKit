using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using UnityToolKit.Runtime.ResourceSystem;

namespace UnityToolKit.Editor.ResourceSystem
{

    [CustomEditor(typeof(ResourceSystemConfig))]
    public class ResourceSystemConfigInspector : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            using (new EditorGUI.DisabledScope(true))
                EditorGUILayout.PropertyField(serializedObject.FindProperty("m_Script"));

            EditorGUILayout.HelpBox("供 ResourceManager 使用时，请保存为 Resources/ResourceSystemConfig.asset。\n"
                + "在主线程转换为 Options；修改资产不会更新已经创建的资源系统。", MessageType.Info);

            DrawReference(serializedObject.FindProperty(nameof(ResourceSystemConfig.ExecutionContext)),
                typeof(ExecutionContextConfig), "执行上下文");
            if (((ResourceSystemConfig)target).ExecutionContext is ImmediateExecutionContextConfig)
                EditorGUILayout.HelpBox("内联上下文不会切换到主线程，仅适用于不调用 Unity API 的加载器、转换器和工厂。", MessageType.Warning);

            DrawList(nameof(ResourceSystemConfig.Loaders), typeof(LoaderConfig), "加载器与来源策略");
            EditorGUILayout.HelpBox("取消“初始化时创建实例”后，不会自动创建加载器，来源、并发和总超时仍会保存。"
                + "外部系统就绪后只注册实例，沿用这里的策略；显式传入策略会覆盖本次注册。", MessageType.Info);
            DrawList(nameof(ResourceSystemConfig.Converters), typeof(ConverterConfig), "转换器（按目标类型注册）");
            EditorGUILayout.HelpBox("列表按从上到下的顺序注册；相同来源或目标类型由后项覆盖前项。", MessageType.Info);

            DrawReference(serializedObject.FindProperty(nameof(ResourceSystemConfig.InstanceFactory)),
                typeof(InstanceFactoryConfig), "实例工厂（未选择时不提供租用）");
            using (new EditorGUI.DisabledScope(serializedObject.FindProperty(nameof(ResourceSystemConfig.InstanceFactory)).managedReferenceValue == null))
                EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(ResourceSystemConfig.MaxIdlePerPrototype)),
                    new GUIContent("每原型闲置上限", "0 表示归还即销毁；只限制闲置实例。"));

            DrawList(nameof(ResourceSystemConfig.Initializers), typeof(InitializerConfig), "初始化准备工作（可选，启动前必须完成）");
            EditorGUILayout.HelpBox("自定义配置：在运行时程序集派生对应 Config，添加 [Serializable] 并提供公开无参构造函数。\n"
                + "远程文件可使用内置 RemoteFileLoaderConfig。AssetBundle 等外部系统由业务提供加载器并管理自身生命周期。", MessageType.Info);

            serializedObject.ApplyModifiedProperties();
            if (SerializationUtility.HasManagedReferencesWithMissingTypes(target))
                EditorGUILayout.HelpBox("存在已丢失的配置类型。请恢复对应脚本，或重新选择配置类型后保存。", MessageType.Error);
            try { ((ResourceSystemConfig)target).Validate(); }
            catch (Exception error) { EditorGUILayout.HelpBox(error.Message, MessageType.Error); }
        }

        private void DrawList(string field, Type baseType, string label)
        {
            var list = serializedObject.FindProperty(field);
            EditorGUILayout.Space();
            EditorGUILayout.LabelField(label, EditorStyles.boldLabel);
            for (var i = 0; i < list.arraySize; i++)
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField($"第 {i + 1} 项");
                        using (new EditorGUI.DisabledScope(i == 0))
                        {
                            if (GUILayout.Button("↑", GUILayout.Width(24)))
                            {
                                list.MoveArrayElement(i, i - 1);
                                break;
                            }
                        }
                        using (new EditorGUI.DisabledScope(i == list.arraySize - 1))
                        {
                            if (GUILayout.Button("↓", GUILayout.Width(24)))
                            {
                                list.MoveArrayElement(i, i + 1);
                                break;
                            }
                        }
                        if (GUILayout.Button("删除", GUILayout.Width(44)))
                        {
                            list.DeleteArrayElementAtIndex(i);
                            break;
                        }
                    }
                    DrawReference(list.GetArrayElementAtIndex(i), baseType, "类型");
                }
            }
            if (GUILayout.Button("添加" + label)) ShowTypeMenu(list.propertyPath, baseType, append: true);
        }

        private void DrawReference(SerializedProperty property, Type baseType, string label)
        {
            var value = property.managedReferenceValue;
            // Foldout 的箭头绘制在标签左侧，整块先缩进一级，为箭头留出空间。
            using (new EditorGUI.IndentLevelScope())
            {
                var row = EditorGUILayout.GetControlRect();
                var labelRect = row;
                labelRect.width = EditorGUIUtility.labelWidth;
                property.isExpanded = EditorGUI.Foldout(labelRect, property.isExpanded, label, true);

                // 使用标准字段的标签宽度，让类型下拉框与下面的参数输入框对齐。
                var typeRect = row;
                typeRect.xMin += EditorGUIUtility.labelWidth;
                var typeLabel = value == null ? "未选择" : ObjectNames.NicifyVariableName(value.GetType().Name);
                if (GUI.Button(typeRect, typeLabel, EditorStyles.popup)) ShowTypeMenu(property.propertyPath, baseType, append: false);
                if (!property.isExpanded || value == null) return;

                using (new EditorGUI.IndentLevelScope())
                {
                    if (value is ConverterConfig converter)
                        EditorGUILayout.LabelField("目标类型", converter.TargetType?.FullName ?? "未指定");
                    var initialize = value is LoaderConfig ? property.FindPropertyRelative(nameof(LoaderConfig.InitializeOnStartup)) : null;
                    var isRemote = value is RemoteFileLoaderConfig;
                    var child = property.Copy();
                    var end = child.GetEndProperty();
                    for (var next = child.NextVisible(true); next && !SerializedProperty.EqualContents(child, end); next = child.NextVisible(false))
                    {
                        var isPolicy = child.name == nameof(LoaderConfig.Source)
                            || child.name == nameof(LoaderConfig.InitializeOnStartup)
                            || child.name == nameof(LoaderConfig.MaxConcurrentLoads)
                            || child.name == nameof(LoaderConfig.TimeoutSeconds);
                        using (new EditorGUI.DisabledScope(initialize != null && !initialize.boolValue && !isPolicy && !isRemote))
                        {
                            var labelText = isRemote ? RemoteFieldLabel(child.name) : null;
                            if (initialize != null && child.name == nameof(LoaderConfig.InitializeOnStartup))
                                labelText = "初始化时创建实例";
                            var tooltip = isRemote && child.name == nameof(LoaderConfig.InitializeOnStartup)
                                ? "仅控制创建时机；关闭后仍可编辑远程参数，供稍后 CreateRemoteLoader 使用。" : child.tooltip;
                            var fieldLabel = labelText == null ? null : new GUIContent(labelText, tooltip);
                            EditorGUILayout.PropertyField(child, fieldLabel, true);
                        }
                    }
                    if (isRemote)
                    {
                        EditorGUILayout.HelpBox(initialize.boolValue
                            ? "初始化时会准备文件缓存；资源系统关闭时会关闭自建缓存和下载器。"
                            : "远程参数仍可编辑。稍后调用 CreateRemoteLoader，先 await InitializeAsync，再 RegisterLoader。"
                              + "调用者拥有该实例，停止使用后需 await DisposeAsync。", MessageType.Info);
                    }
                }
            }
        }

        private static string RemoteFieldLabel(string field)
        {
            switch (field)
            {
                case nameof(LoaderConfig.Source): return "资源来源";
                case nameof(LoaderConfig.MaxConcurrentLoads): return "同时加载上限";
                case nameof(LoaderConfig.TimeoutSeconds): return "请求总超时（秒）";
                case nameof(RemoteFileLoaderConfig.CacheDirectory): return "缓存目录";
                case nameof(RemoteFileLoaderConfig.StartupTargetBytes): return "启动清理目标（字节）";
                case nameof(RemoteFileLoaderConfig.MaxAgeSeconds): return "文件有效期（秒）";
                case nameof(RemoteFileLoaderConfig.MaxConcurrentDownloads): return "同时下载上限";
                case nameof(RemoteFileLoaderConfig.MaxRetries): return "最大重试次数";
                case nameof(RemoteFileLoaderConfig.RetryDelayMs): return "首次重试等待（毫秒）";
                case nameof(RemoteFileLoaderConfig.MaxRetryDelayMs): return "重试等待上限（毫秒）";
                case nameof(RemoteFileLoaderConfig.BufferSize): return "读写缓冲区（字节）";
                case nameof(RemoteFileLoaderConfig.ConnectTimeoutMs): return "连接超时（毫秒）";
                case nameof(RemoteFileLoaderConfig.ReadTimeoutMs): return "读取超时（毫秒）";
                case nameof(RemoteFileLoaderConfig.MaxBytesPerSecond): return "每任务限速（字节/秒）";
                default: return null;
            }
        }

        private void ShowTypeMenu(string propertyPath, Type baseType, bool append)
        {
            var asset = (ResourceSystemConfig)target;
            var menu = new GenericMenu();
            if (!append) menu.AddItem(new GUIContent("未选择"), false, () => SetType(asset, propertyPath, null, false));

            // 排除 Editor 专用与测试程序集；PlayMode 测试也能面向 Player 编译，不能使用普通 Player 列表。
            var playerAssemblies = new HashSet<string>();
            foreach (var assembly in CompilationPipeline.GetAssemblies(AssembliesType.PlayerWithoutTestAssemblies))
            {
                playerAssemblies.Add(assembly.name);
                foreach (var reference in assembly.compiledAssemblyReferences)
                    playerAssemblies.Add(Path.GetFileNameWithoutExtension(reference));
            }
            var types = TypeCache.GetTypesDerivedFrom(baseType)
                .Prepend(baseType)
                .Where(type => !type.IsAbstract && !type.ContainsGenericParameters && type.IsSerializable
                    && type.GetConstructor(Type.EmptyTypes) != null && playerAssemblies.Contains(type.Assembly.GetName().Name))
                .OrderBy(type => type.FullName);
            var found = false;
            foreach (var type in types)
            {
                found = true;
                var label = type == typeof(LoaderConfig) ? "加载器配置（仅策略）"
                    : type.FullName.Replace('.', '/').Replace('+', '/');
                menu.AddItem(new GUIContent(label), false,
                    () => SetType(asset, propertyPath, type, append));
            }
            if (!found) menu.AddDisabledItem(new GUIContent("没有可用配置类型，请添加运行时配置类"));
            menu.ShowAsContext();
        }

        private static void SetType(ResourceSystemConfig asset, string propertyPath, Type type, bool append)
        {
            if (asset == null) return;
            try
            {
                var value = type == null ? null : Activator.CreateInstance(type);
                if (type == typeof(LoaderConfig)) ((LoaderConfig)value).InitializeOnStartup = false;
                using (var serialized = new SerializedObject(asset))
                {
                    var property = serialized.FindProperty(propertyPath);
                    if (!append && property.managedReferenceValue?.GetType() == type) return;
                    if (append)
                    {
                        property.arraySize++;
                        property = property.GetArrayElementAtIndex(property.arraySize - 1);
                    }
                    property.managedReferenceValue = value;
                    property.isExpanded = true;
                    serialized.ApplyModifiedProperties();
                }
            }
            catch (Exception error)
            {
                Debug.LogException(new InvalidOperationException("无法创建配置 " + type?.FullName, error), asset);
            }
        }
    }
}
