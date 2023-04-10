using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using Bonsai.Core;
using Bonsai.Utility;
using Unity.VisualScripting;
using UnityEditor;
using UnityEngine;

namespace Bonsai.Designer
{
    using FilePanelResult = Utility.Result<FilePanelError, string>;

    enum FilePanelError
    {
        Cancel,
        InvalidPath
    }

    /// <summary>
    /// Handles the saving and loading of tree assets.
    /// </summary>
    public class BonsaiSaver
    {
        public struct TreeMetaData
        {
            public Vector2 zoom;
            public Vector2 pan;
        }

        public event EventHandler<string> SaveMessage;

        public event EventHandler<bool> OnSaveDone;

        public string JsonOutputPath;

        // Tree is valid and exists in the Asset database.
        public bool CanSaveTree(BehaviourTree tree)
        {
            return tree != null && AssetDatabase.Contains(tree.Proxy);
        }

        /// <summary>
        /// Prompts user to load a tree from file.
        /// </summary>
        /// <returns>The behaviour tree from the asset file. Null if load failed.</returns>
        public BehaviourTree LoadBehaviourTree()
        {
            FilePanelResult path = GetCanvasOpenFilePath();

            if (path.Success)
            {
                var tree = LoadBehaviourTree(path.Value);
                if (tree == null)
                {
                    OnLoadFailure();
                }
                else
                {
                    OnLoadSuccess();
                }

                return tree;
            }
            else
            {
                OnInvalidPathError(path.Error);
                return null;
            }
        }

        /// <summary>
        /// Saves the behaviour tree from the canvas.
        /// If the tree is unsaved (new) then it prompts the user to specify a file to save.
        /// </summary>
        public void SaveCanvas(BonsaiCanvas canvas, TreeMetaData meta, bool lastSave = false)
        {
            // Tree is new, need to save to asset database.
            if (!AssetDatabase.Contains(canvas.Tree.Proxy))
            {
                GetSaveFilePath()
                    .OnSuccess(savePath =>
                    {
                        SaveNewTree(savePath, meta, canvas, lastSave);
                        OnTreeSaved();
                    })
                    .OnFailure(OnInvalidPathError);
            }
            else // Tree is already saved. Save nodes and tree data.
            {
                SaveTree(meta, canvas, lastSave);
                OnTreeSaved();
            }
        }

        /// <summary>
        /// Creates a new Behaviour Tree instance with a blackboard.
        /// The tree has no BehaviourNodes and no root node. The instance is unsaved.
        /// </summary>
        /// <returns>The new Behaviour Tree with a blackboard set.</returns>
        public static BehaviourTree CreateBehaviourTree()
        {
            var treeProxy = ScriptableObject.CreateInstance<BehaviourTreeProxy>();
            CreateBlackboard(treeProxy);
            return treeProxy.Tree;
        }

        /// <summary>
        /// Creates a new blackboard.
        /// </summary>
        private static BlackboardProxy CreateBlackboard(BehaviourTreeProxy treeProxy)
        {
            var blackboardProxy = ScriptableObject.CreateInstance<BlackboardProxy>();
            blackboardProxy.AttachToBehaviourTree(treeProxy);
            blackboardProxy.hideFlags = HideFlags.HideInHierarchy;
            return blackboardProxy;
        }

        // Load a behaviour tree at the given path. The path is aboslute but the file must be under the Asset's folder.
        private static BehaviourTree LoadBehaviourTree(string absolutePath)
        {
            string path = AssetPath(absolutePath);
            var tree = AssetDatabase.LoadAssetAtPath<BehaviourTreeProxy>(path).Tree;
            // Add a blackboard if missing when opening in editor.
            AddBlackboardIfMissing(tree);
            return tree;
        }

        public static void AddBlackboardIfMissing(BehaviourTree tree)
        {
            if (tree == null)
            {
                return;
            }

            // no blackboard or no asset
            if (tree.Blackboard == null ||
                (tree.Blackboard.Proxy != null && !AssetDatabase.Contains(tree.Blackboard.Proxy)))
            {
                if (tree.Blackboard == null)
                {
                    CreateBlackboard(tree.Proxy);
                }

                AssetDatabase.AddObjectToAsset(tree.Blackboard.Proxy, tree.Proxy);
            }
            else
            {
                // blackboard lost contact with it's asset, fix it.
                if (tree.Blackboard.Proxy == null)
                {
                    var blackboardProxy =
                        EditorUtility.InstanceIDToObject(tree.Blackboard.AssetInstanceID) as BlackboardProxy;
                    tree.Blackboard.Proxy = blackboardProxy;
                }
            }
        }

        // Adds the tree to the database and saves the nodes to the database.
        private void SaveNewTree(string path, TreeMetaData meta, BonsaiCanvas canvas, bool lastSave = false)
        {
            // Save tree and black board assets
            var treeProxy = canvas.Tree.Proxy;
            AssetDatabase.CreateAsset(treeProxy, path);
            treeProxy.SetName();
            AssetDatabase.AddObjectToAsset(canvas.Tree.Blackboard.Proxy, treeProxy);
            // Save nodes.
            SaveTree(meta, canvas, lastSave);
        }

        // Saves the current tree and nodes.
        private void SaveTree(TreeMetaData meta, BonsaiCanvas canvas, bool lastSave = false)
        {
            // If the blackboard is not yet in the database, then add.
            AddBlackboardIfMissing(canvas.Tree);

            var canvasBehaviours = canvas.Nodes.Select(n => n.Behaviour);

            AddNewNodeAssets(canvas.Tree, canvasBehaviours);

            // Clear all parent-child connections. These will be reconstructed to match the connection in the BonsaiNodes.
            canvas.Tree.ClearStructure();

            // Sort the canvas.
            // Only consider nodes with 2 or more children for sorting.
            foreach (BonsaiNode node in canvas.Nodes.Where(node => node.ChildCount() > 1))
            {
                node.SortChildren();
            }

            // Set parent-child connections matching those in the canvas. Only consider decorators and composites.
            SetCompositeChildren(canvas);
            SetDecoratorChildren(canvas);

            // Re-add nodes to tree.
            if (canvas.Root != null)
            {
                canvas.Tree.SetNodes(canvas.Root.Behaviour);
            }

            // Nodes not connected to he root will have an unset pre-order index.
            // Tree.ClearStructure unsets the index and is only set in Tree.SetNodes
            // for nodes under the root.
            canvas.Tree.Proxy.UnusedNodes = canvasBehaviours.Where(
                b => b.PreOrderIndex == BehaviourNode.KInvalidOrder).ToList();

            SaveTreeToJson(canvas);
            SaveTreeMetaData(meta, canvas);
            if (lastSave)
            {
                ClearJsonData(canvas.Tree.Proxy);
            }

            AssetDatabase.SaveAssets();
            Log.LogInfo("Save behaviour tree success!");
        }

        private void SaveTreeToJson(BonsaiCanvas canvas)
        {
            if (string.IsNullOrEmpty(JsonOutputPath))
            {
                if (string.IsNullOrEmpty(canvas.Tree.Proxy.JsonPath))
                {
                    FileControllerUtls.Instance.ChooseDirectory();
                    JsonOutputPath = FileControllerUtls.Instance.ChooseDirPath;
                }
                else
                {
                    JsonOutputPath = Path.GetDirectoryName(canvas.Tree.Proxy.JsonPath);
                }
            }

            CheckName(canvas);
            var jsonStr = SerializeHelper.SerializeObject(canvas.Tree);
            var jsonPath = Path.Combine(JsonOutputPath, canvas.Tree.name + ".json");
            FileHelper.WriteFile(jsonPath, jsonStr);
            canvas.Tree.Proxy.JsonPath = jsonPath;
        }

        private void ClearJsonData(BehaviourTreeProxy treeProxy)
        {
            var path = AssetDatabase.GetAssetPath(treeProxy);
            var subAssets = AssetDatabase.LoadAllAssetRepresentationsAtPath(path);
            foreach (var asset in subAssets)
            {
                if (asset is BehaviourNodeProxy behaviourNodeProxy)
                {
                    Log.LogInfo($"Remove Json data in {behaviourNodeProxy.name}!");
                    behaviourNodeProxy.Node = null;
                }
                else if (asset is BlackboardProxy blackboardProxy)
                {
                    Log.LogInfo($"Remove Json data in BlackboardProxy!");
                    blackboardProxy.Blackboard = null;
                }
            }

            treeProxy.Tree = null;
        }

        private void CheckName(BonsaiCanvas canvas)
        {
            if (string.IsNullOrEmpty(canvas.Tree.name))
            {
                canvas.Tree.Proxy.SetName();
            }
        }

        private void SetCompositeChildren(BonsaiCanvas canvas)
        {
            IEnumerable<BonsaiNode> compositeNodes = canvas.Nodes.Where(n => n.Behaviour.IsComposite());
            foreach (BonsaiNode node in compositeNodes)
            {
                var compositeBehaviour = node.Behaviour as Composite;
                compositeBehaviour.SetChildren(node.Children.Select(ch => ch.Behaviour).ToArray());
            }
        }

        private void SetDecoratorChildren(BonsaiCanvas canvas)
        {
            IEnumerable<BonsaiNode> decoratorNodes = canvas.Nodes
                .Where(n => n.Behaviour.IsDecorator() && n.ChildCount() == 1);

            foreach (BonsaiNode node in decoratorNodes)
            {
                var decoratorBehaviour = node.Behaviour as Decorator;
                decoratorBehaviour.SetChild(node.GetChildAt(0).Behaviour);
            }
        }

        private void AddNewNodeAssets(BehaviourTree treeAsset, IEnumerable<BehaviourNode> canvasNodes)
        {
            foreach (BehaviourNode node in canvasNodes)
            {
                if (!AssetDatabase.Contains(node.Proxy))
                {
                    node.name = node.GetType().Name;
                    // node.Proxy.hideFlags = HideFlags.HideInHierarchy;
                    AssetDatabase.AddObjectToAsset(node.Proxy, treeAsset.Proxy);
                }
            }
        }

        private void SaveTreeMetaData(TreeMetaData meta, BonsaiCanvas canvas)
        {
            foreach (var editorNode in canvas.Nodes)
            {
                editorNode.Behaviour.Proxy.bonsaiNodePosition = editorNode.Position;
            }

            canvas.Tree.Proxy.panPosition = meta.pan;
            canvas.Tree.Proxy.zoomPosition = meta.zoom;
        }

        /// <summary>
        /// Gets the file path to save the canavs at.
        /// </summary>
        /// <returns></returns>
        private FilePanelResult GetSaveFilePath()
        {
            string path = EditorUtility.SaveFilePanelInProject("Save Bonsai Canvas", "NewBonsaiBT", "asset",
                "Select a destination to save the canvas.");

            if (string.IsNullOrEmpty(path))
            {
                return FilePanelResult.Fail(FilePanelError.Cancel);
            }

            return FilePanelResult.Ok(path);
        }

        /// <summary>
        /// Get the path from open file dialog.
        /// </summary>
        /// <returns></returns>
        private FilePanelResult GetCanvasOpenFilePath()
        {
            string path = EditorUtility.OpenFilePanel("Open Bonsai Canvas", "Assets/", "asset");

            if (string.IsNullOrEmpty(path))
            {
                return FilePanelResult.Fail(FilePanelError.Cancel);
            }

            // If the path is outside the project's asset folder.
            if (!path.Contains(Application.dataPath))
            {
                return FilePanelResult.Fail(FilePanelError.InvalidPath);
            }


            return FilePanelResult.Ok(path);
        }

        /// <summary>
        /// Converts the absolute path to a path relative to the Assets folder.
        /// </summary>
        private static string AssetPath(string absolutePath)
        {
            int assetIndex = absolutePath.IndexOf("/Assets/");
            return absolutePath.Substring(assetIndex + 1);
        }


        private void OnInvalidPathError(FilePanelError error)
        {
            if (error == FilePanelError.InvalidPath)
            {
                SaveMessage?.Invoke(this, "Please select a Bonsai asset within the project's Asset folder.");
            }
        }

        private void OnLoadFailure()
        {
            SaveMessage?.Invoke(this, "Failed to load tree.");
        }

        private void OnLoadSuccess()
        {
            SaveMessage?.Invoke(this, "Tree loaded");
        }

        private void OnTreeSaved()
        {
            SaveMessage?.Invoke(this, "Tree Saved");
            OnSaveDone?.Invoke(this, true);
        }

        private void OnTreeCopied()
        {
            SaveMessage?.Invoke(this, "Tree Copied");
        }
    }
}