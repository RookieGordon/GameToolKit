/*
 * 功能描述：编辑器窗口样式定义
 */

using UnityEditor;
using UnityEngine;

namespace UnityToolKit.Editor.Common
{
    public static class WindowLabelStyle
    {
        private static GUIStyle _errorLabel;

        public static GUIStyle ErrorLabel
        {
            get
            {
                if (_errorLabel == null)
                {
                    _errorLabel = new GUIStyle(EditorStyles.label)
                    {
                        richText = true,
                        fontSize = 14,
                        fontStyle = FontStyle.Bold,
                    };
                }

                return _errorLabel;
            }
        }
    }
}
