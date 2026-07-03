using System;
using ToolKit.Tools.Common;
using UnityEngine;
using UnityToolKit.Runtime.Resource;

namespace Tests.ResourceTest
{
    public sealed class ResourceSystemManualTest : MonoBehaviour
    {
        private const string TextAddress = "UResourceManagerTest/TestText";
        private const string MissingAddress = "UResourceManagerTest/MissingText";

        private readonly ManualTextApplicator _applicator = new ManualTextApplicator();
        private ManualApplyTarget _target;
        private ResourceRef _loadedRef;
        private Vector2 _scroll;
        private bool _busy;
        private string _status = "Ready. Press Play, then click a button.";
        private string _lastLoadedText = "";
        private string _lastError = "";

        private void Awake()
        {
            _target = ScriptableObject.CreateInstance<ManualApplyTarget>();
            _target.name = "ResourceSystemManualTarget";
        }

        private void OnDestroy()
        {
            _loadedRef?.Dispose();
            _loadedRef = null;

            if (_target != null)
            {
                Destroy(_target);
            }
        }

        private void OnGUI()
        {
            var width = Mathf.Min(720f, Screen.width - 40f);
            GUILayout.BeginArea(new Rect(20f, 20f, width, Screen.height - 40f), GUI.skin.box);
            _scroll = GUILayout.BeginScrollView(_scroll);

            GUILayout.Label("UnityToolKit Resource System Manual Test");
            GUILayout.Space(8f);

            GUI.enabled = !_busy;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Load Resources Text", GUILayout.Height(32f)))
            {
                LoadText();
            }

            if (GUILayout.Button("Apply Text To Target", GUILayout.Height(32f)))
            {
                ApplyText();
            }

            if (GUILayout.Button("Load Missing Resource", GUILayout.Height(32f)))
            {
                LoadMissing();
            }

            if (GUILayout.Button("Revert Target", GUILayout.Height(32f)))
            {
                RevertTarget();
            }
            GUILayout.EndHorizontal();
            GUI.enabled = true;

            GUILayout.Space(12f);
            DrawField("Status", _status);
            DrawField("Loaded Ref Valid", _loadedRef != null && _loadedRef.IsValid ? "True" : "False");
            DrawField("Apply Count", _applicator.ApplyCount.ToString());
            DrawField("Revert Count", _applicator.RevertCount.ToString());
            DrawField("Target Content", string.IsNullOrEmpty(_target.Content) ? "<empty>" : _target.Content);
            DrawField("Last Loaded Text", string.IsNullOrEmpty(_lastLoadedText) ? "<empty>" : _lastLoadedText);
            DrawField("Last Error", string.IsNullOrEmpty(_lastError) ? "<none>" : _lastError);

            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        private static void DrawField(string label, string value)
        {
            GUILayout.Label(label);
            GUILayout.TextArea(value, GUILayout.MinHeight(34f));
            GUILayout.Space(6f);
        }

        // 手动测试点：验证 Resources 成功加载，以及 ResourceRef 持有状态是否正确。
        private void LoadText()
        {
            _busy = true;
            _status = "Loading Resources text...";
            _lastError = "";
            _lastLoadedText = "";
            _loadedRef?.Dispose();
            _loadedRef = null;

            GResourceManager.Instance.LoadResourceAsync(TextAddress, OnLoadSucceeded, OnLoadFailed);
        }

        // 手动测试点：验证 Resources 地址不存在时能显示可读的 LoadError。
        private void LoadMissing()
        {
            _busy = true;
            _status = "Loading missing Resources address...";
            _lastError = "";
            _loadedRef?.Dispose();
            _loadedRef = null;

            GResourceManager.Instance.LoadResourceAsync(MissingAddress, OnLoadSucceeded, OnLoadFailed);
        }

        // 手动测试点：验证 ApplyResourceAsync 能把加载到的 TextAsset 写入目标对象。
        private void ApplyText()
        {
            _busy = true;
            _status = "Applying Resources text to target...";
            _lastError = "";

            GResourceManager.Instance.ApplyResourceAsync<ManualApplyTarget, TextAsset>(
                _target,
                TextAddress,
                _applicator,
                () =>
                {
                    _busy = false;
                    _status = "Apply finished. Target Content should contain the test payload.";
                },
                default,
                DateTime.Now.ToString("HH:mm:ss"));
        }

        // 手动测试点：验证 RevertAsset 能清空已经应用到目标对象上的状态。
        private void RevertTarget()
        {
            GResourceManager.Instance.RevertAsset<ManualApplyTarget>(_target, _applicator);
            _status = "Target reverted.";
        }

        private void OnLoadSucceeded(ResourceRef resourceRef)
        {
            _loadedRef?.Dispose();
            _loadedRef = resourceRef;

            var textAsset = resourceRef.GetTextAsset();
            _lastLoadedText = textAsset != null ? textAsset.text : "<loaded ref did not contain TextAsset>";
            _busy = false;
            _status = "Load succeeded. ResourceRef is held until another load or scene destroy.";
        }

        private void OnLoadFailed(LoadError error)
        {
            _lastError = $"{error.Code}: {error.Message}";
            _busy = false;
            _status = "Load failed. This is expected for the missing-resource button.";
        }

        private sealed class ManualApplyTarget : ScriptableObject
        {
            public string Content;
        }

        private sealed class ManualTextApplicator : IApplicable
        {
            public int ApplyCount { get; private set; }
            public int RevertCount { get; private set; }

            public void Apply<T, R>(T target, R resource, params object[] applayArgs)
                where T : class
                where R : class
            {
                if (target is not ManualApplyTarget applyTarget || resource is not TextAsset textAsset)
                {
                    return;
                }

                ApplyCount++;
                var tag = applayArgs != null && applayArgs.Length > 0 ? applayArgs[0] as string : null;
                applyTarget.Content = string.IsNullOrEmpty(tag) ? textAsset.text : $"{tag}\n{textAsset.text}";
            }

            public void Revert<T>(T target) where T : class
            {
                if (target is not ManualApplyTarget applyTarget)
                {
                    return;
                }

                RevertCount++;
                applyTarget.Content = "";
            }
        }
    }
}
