/*
 * author       : Gordon
 * datetime     : 2025/4/1
 * description  : 文件路径扩展工具
 */

using System;
using System.IO;

namespace ToolKit.Tools.Common
{
    public class PathHelper
    {
        /// <summary>
        /// 是否是无效路径
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        private static bool _IsInvalidPath(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return true;
            }

            if (path.IndexOf('/') == -1 && path.IndexOf('\\') == -1)
            {
                return true;
            }

            return false;
        }

        private static void _CheckInvalidPath(string path)
        {
            if (_IsInvalidPath(path))
            {
                throw new ArgumentException("Invalid path!");
            }
        }

        /// <summary>
        /// 是否是文件路径
        /// </summary>
        private static bool _IsFilePath(string path)
        {
            var ex = Path.GetExtension(path);
            return !string.IsNullOrEmpty(ex);
        }
        
        /// <summary>
        /// 是否是文件夹路径
        /// </summary>
        private static bool _IsDirectoryPath(string path)
        {
            path = path.Replace('/', '\\');
            return path[^1] == '\\';
        }

        /// <summary>
        /// 获取文件的名字
        /// </summary>
        public static string GetFileName(string path, bool includeExtension = true)
        {
            _CheckInvalidPath(path);
            return includeExtension ? Path.GetFileName(path) : Path.GetFileNameWithoutExtension(path);
        }

        /// <summary>
        /// 获取父文件夹的名字
        /// </summary>
        public static string GetDirectoryName(string path, bool isValidPath = false)
        {
            _CheckInvalidPath(path);
            if (_IsFilePath(path))
            {
                var l = path.Replace('/', '\\').Split('\\');
                return l[^2];
            }
            
            path = path.Replace('/', '\\');
            if (_IsDirectoryPath(path))
            {
                var l = path.Split('\\');
                return l[^1];
            }
            
            // 外部指认为是合法路径，那么就只能是去掉了后缀的文件路径
            if (isValidPath)
            {
                var l = path.Split('\\');
                return l[^2];
            }

            return string.Empty;
        }

        /// <summary>
        /// 获取父文件夹的路径
        /// </summary>
        public static string GetDirectoryPath(string path)
        {
            _CheckInvalidPath(path);
            return Path.GetDirectoryName(path);
        }
        
        /// <summary>
        /// 去掉文件名的后缀
        /// </summary>
        public static string RemoveExtension(string path)
        {
            _CheckInvalidPath(path);
            if (!_IsFilePath(path))
            {
                throw new ArgumentException("Invalid path!");
            }
            return Path.ChangeExtension(path, null);
        }
    }
}