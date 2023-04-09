using UnityEngine;
using System.Collections;
using System.Runtime.InteropServices;
using System;

namespace Bonsai.Utility
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public class OpenDialogFile
    {
        public int structSize = 0;
        public IntPtr dlgOwner = IntPtr.Zero;
        public IntPtr instance = IntPtr.Zero;
        public String filter = null;
        public String customFilter = null;
        public int maxCustFilter = 0;
        public int filterIndex = 0;
        public String file = null;
        public int maxFile = 0;
        public String fileTitle = null;
        public int maxFileTitle = 0;
        public String initialDir = null;
        public String title = null;
        public int flags = 0;
        public short fileOffset = 0;
        public short fileExtension = 0;
        public String defExt = null;
        public IntPtr custData = IntPtr.Zero;
        public IntPtr hook = IntPtr.Zero;
        public String templateName = null;
        public IntPtr reservedPtr = IntPtr.Zero;
        public int reservedInt = 0;
        public int flagsEx = 0;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public class OpenDialogDir
    {
        public IntPtr hwndOwner = IntPtr.Zero;
        public IntPtr pidlRoot = IntPtr.Zero;
        public String pszDisplayName = "123";
        public String lpszTitle = null;
        public UInt32 ulFlags = 0;
        public IntPtr lpfn = IntPtr.Zero;
        public IntPtr lParam = IntPtr.Zero;
        public int iImage = 0;
    }

    public class DllOpenFileDialog
    {
        [DllImport("Comdlg32.dll", SetLastError = true, ThrowOnUnmappableChar = true, CharSet = CharSet.Auto)]
        public static extern bool GetOpenFileName([In, Out] OpenDialogFile ofn);

        [DllImport("Comdlg32.dll", SetLastError = true, ThrowOnUnmappableChar = true, CharSet = CharSet.Auto)]
        public static extern bool GetSaveFileName([In, Out] OpenDialogFile ofn);

        [DllImport("shell32.dll", SetLastError = true, ThrowOnUnmappableChar = true, CharSet = CharSet.Auto)]
        public static extern IntPtr SHBrowseForFolder([In, Out] OpenDialogDir ofn);

        [DllImport("shell32.dll", SetLastError = true, ThrowOnUnmappableChar = true, CharSet = CharSet.Auto)]
        public static extern bool SHGetPathFromIDList([In] IntPtr pidl, [In, Out] char[] fileName);
    }


    public class FileControllerUtls
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetActiveWindow();

        private static FileControllerUtls _instance;

        public static FileControllerUtls Instance => _instance ?? (_instance = new FileControllerUtls());


        public string ChooseDirPath; //保存文件的文件名


        public void ChooseDirectory()
        {
            OpenDialogDir openDir = new OpenDialogDir();
            openDir.pszDisplayName = new string(new char[2000]);
            openDir.lpszTitle = "资源文件夹选择";
            openDir.ulFlags = 1; // BIF_NEWDIALOGSTYLE | BIF_EDITBOX;
            IntPtr pidl = DllOpenFileDialog.SHBrowseForFolder(openDir);

            char[] path = new char[2000];
            for (int i = 0; i < 2000; i++)
                path[i] = '\0';
            if (DllOpenFileDialog.SHGetPathFromIDList(pidl, path))
            {
                string str = new string(path);
                string DirPath = str.Substring(0, str.IndexOf('\0'));
                ChooseDirPath = DirPath;
                Log.LogInfo("路径" + DirPath);
            }
        }
    }
}