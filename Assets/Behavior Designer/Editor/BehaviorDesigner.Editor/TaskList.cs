// Decompiled with JetBrains decompiler
// Type: BehaviorDesigner.Editor.TaskList
// Assembly: BehaviorDesigner.Editor, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: 1F1EBCA8-62DA-44C1-B5C8-3A2E0B1DB57B
// Assembly location: D:\Workspace\Reference\GameToolKit\Assets\Behavior Designer\Editor\BehaviorDesigner.Editor.dll

using BehaviorDesigner.Runtime;
using BehaviorDesigner.Runtime.Tasks;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace BehaviorDesigner.Editor
{
    [Serializable]
    public class TaskList : ScriptableObject
    {
        private List<TaskList.CategoryList> mCategoryList;
        private List<TaskList.CategoryList> mQuickCategoryList;
        private Dictionary<System.Type, TaskNameAttribute[]> mTaskNameAttribute = new Dictionary<System.Type, TaskNameAttribute[]>();
        private Vector2 mScrollPosition = Vector2.zero;
        private string mSearchString = string.Empty;
        private bool mFocusSearch;
        private Vector2 mQuickScrollPosition = Vector2.zero;
        private string mQuickSearchString = string.Empty;
        private bool mFocusQuickSearch;
        private int mSelectedQuickIndex;
        private System.Type mSelectedQuickIndexType;
        private int mQuickIndexCount;

        public void OnEnable() => this.hideFlags = HideFlags.HideAndDontSave;

        public void Init()
        {
            this.mCategoryList = new List<TaskList.CategoryList>();
            this.mQuickCategoryList = new List<TaskList.CategoryList>();
            List<System.Type> typeList = new List<System.Type>();
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    System.Type[] types = assembly.GetTypes();
                    for (int index = 0; index < types.Length; ++index)
                    {
                        if (typeof(Task).IsAssignableFrom(types[index]) && !types[index].Equals(typeof(BehaviorReference)) && !types[index].IsAbstract &&
                            (types[index].IsSubclassOf(typeof(Runtime.Tasks.Action)) || types[index].IsSubclassOf(typeof(Composite)) || types[index].IsSubclassOf(typeof(Conditional)) || types[index].IsSubclassOf(typeof(Decorator))))
                            typeList.Add(types[index]);
                    }
                }
                catch (Exception ex)
                {
                }
            }

            typeList.Sort((IComparer<System.Type>)new AlphanumComparator<System.Type>());
            Dictionary<string, TaskList.CategoryList> dictionary1 = new Dictionary<string, TaskList.CategoryList>();
            Dictionary<string, TaskList.CategoryList> dictionary2 = new Dictionary<string, TaskList.CategoryList>();
            string empty1 = string.Empty;
            int id = 0;
            for (int index1 = 0; index1 < typeList.Count; ++index1)
            {
                string str = !typeList[index1].IsSubclassOf(typeof(Runtime.Tasks.Action)) ? (!typeList[index1].IsSubclassOf(typeof(Composite)) ? (!typeList[index1].IsSubclassOf(typeof(Conditional)) ? "Decorators" : "Conditionals") : "Composites") : "Actions";
                TaskCategoryAttribute[] customAttributes;
                if ((customAttributes = typeList[index1].GetCustomAttributes(typeof(TaskCategoryAttribute), true) as TaskCategoryAttribute[]).Length > 0)
                    str = str + "/" + customAttributes[0].Category.TrimEnd(TaskUtility.TrimCharacters);
                string empty2 = string.Empty;
                string[] strArray = str.Split('/');
                TaskList.CategoryList categoryList1 = (TaskList.CategoryList)null;
                TaskList.CategoryList categoryList2 = (TaskList.CategoryList)null;
                for (int index2 = 0; index2 < strArray.Length; ++index2)
                {
                    if (index2 > 0)
                        empty2 += "/";
                    empty2 += strArray[index2];
                    TaskList.CategoryList category1;
                    TaskList.CategoryList category2;
                    if (!dictionary1.ContainsKey(empty2))
                    {
                        category1 = new TaskList.CategoryList(strArray[index2], empty2, this.PreviouslyExpanded(id), id++);
                        category2 = new TaskList.CategoryList(strArray[index2], empty2, true, 0);
                        if (categoryList1 == null)
                        {
                            this.mCategoryList.Add(category1);
                            this.mQuickCategoryList.Add(category2);
                        }
                        else
                        {
                            categoryList1.AddSubcategory(category1);
                            categoryList2.AddSubcategory(category2);
                        }

                        dictionary1.Add(empty2, category1);
                        dictionary2.Add(empty2, category2);
                    }
                    else
                    {
                        category1 = dictionary1[empty2];
                        category2 = dictionary2[empty2];
                    }

                    categoryList1 = category1;
                    categoryList2 = category2;
                }

                dictionary1[empty2].AddTask(typeList[index1]);
                dictionary2[empty2].AddTask(typeList[index1]);
            }

            this.Search(BehaviorDesignerUtility.SplitCamelCase(this.mSearchString).ToLower().Replace(" ", string.Empty), this.mCategoryList);
            this.Search(BehaviorDesignerUtility.SplitCamelCase(this.mQuickSearchString).ToLower().Replace(" ", string.Empty), this.mQuickCategoryList);
        }

        public void AddTasksToMenu(
            ref GenericMenu genericMenu,
            System.Type selectedTaskType,
            string parentName,
            GenericMenu.MenuFunction2 menuFunction)
        {
            this.AddCategoryTasksToMenu(ref genericMenu, this.mCategoryList, selectedTaskType, parentName, menuFunction);
        }

        public void AddTaskTypesToMenu(
            int typeIndex,
            ref GenericMenu genericMenu,
            System.Type selectedTaskType,
            System.Type ignoredTaskType,
            string parentName,
            bool includeFullPath,
            GenericMenu.MenuFunction2 menuFunction)
        {
            if (typeIndex < 0 || typeIndex >= this.mCategoryList.Count)
                return;
            if (this.mCategoryList[typeIndex].Tasks != null)
            {
                for (int index = 0; index < this.mCategoryList[typeIndex].Tasks.Count; ++index)
                {
                    if (!(this.mCategoryList[typeIndex].Tasks[index].Type == ignoredTaskType))
                    {
                        if (parentName.Equals(string.Empty))
                        {
                            if (includeFullPath)
                                genericMenu.AddItem(new GUIContent(string.Format("{0}/{1}", (object)this.mCategoryList[typeIndex].Fullpath, (object)this.mCategoryList[typeIndex].Tasks[index].Name.ToString())),
                                    this.mCategoryList[typeIndex].Tasks[index].Type.Equals(selectedTaskType),
                                    menuFunction,
                                    (object)this.mCategoryList[typeIndex].Tasks[index].Type);
                            else
                                genericMenu.AddItem(new GUIContent(this.mCategoryList[typeIndex].Tasks[index].Name.ToString()), this.mCategoryList[typeIndex].Tasks[index].Type.Equals(selectedTaskType), menuFunction, (object)this.mCategoryList[typeIndex].Tasks[index].Type);
                        }
                        else if (includeFullPath)
                            genericMenu.AddItem(new GUIContent(string.Format("{0}/{1}/{2}", (object)parentName, (object)this.mCategoryList[typeIndex].Fullpath, (object)this.mCategoryList[typeIndex].Tasks[index].Name.ToString())),
                                this.mCategoryList[typeIndex].Tasks[index].Type.Equals(selectedTaskType),
                                menuFunction,
                                (object)this.mCategoryList[typeIndex].Tasks[index].Type);
                        else
                            genericMenu.AddItem(new GUIContent(string.Format("{0}/{1}", (object)parentName, (object)this.mCategoryList[typeIndex].Tasks[index].Name.ToString())),
                                this.mCategoryList[typeIndex].Tasks[index].Type.Equals(selectedTaskType),
                                menuFunction,
                                (object)this.mCategoryList[typeIndex].Tasks[index].Type);
                    }
                }
            }

            this.AddCategoryTasksToMenu(ref genericMenu, this.mCategoryList[typeIndex].Subcategories, selectedTaskType, parentName, menuFunction);
        }

        private void AddCategoryTasksToMenu(
            ref GenericMenu genericMenu,
            List<TaskList.CategoryList> categoryList,
            System.Type selectedTaskType,
            string parentName,
            GenericMenu.MenuFunction2 menuFunction)
        {
            for (int index1 = 0; index1 < categoryList.Count; ++index1)
            {
                if (categoryList[index1].Subcategories != null)
                    this.AddCategoryTasksToMenu(ref genericMenu, categoryList[index1].Subcategories, selectedTaskType, parentName, menuFunction);
                if (categoryList[index1].Tasks != null)
                {
                    for (int index2 = 0; index2 < categoryList[index1].Tasks.Count; ++index2)
                    {
                        if (parentName.Equals(string.Empty))
                            genericMenu.AddItem(new GUIContent(string.Format("{0}/{1}", (object)categoryList[index1].Fullpath, (object)categoryList[index1].Tasks[index2].Name.ToString())),
                                categoryList[index1].Tasks[index2].Type.Equals(selectedTaskType),
                                menuFunction,
                                (object)categoryList[index1].Tasks[index2].Type);
                        else
                            genericMenu.AddItem(new GUIContent(string.Format("{0}/{1}/{2}", (object)parentName, (object)categoryList[index1].Fullpath, (object)categoryList[index1].Tasks[index2].Name.ToString())),
                                categoryList[index1].Tasks[index2].Type.Equals(selectedTaskType),
                                menuFunction,
                                (object)categoryList[index1].Tasks[index2].Type);
                    }
                }
            }
        }

        public void FocusSearchField(bool quickTaskList, bool clearQuickSearchString)
        {
            if (quickTaskList)
            {
                this.mFocusQuickSearch = true;
                if (!clearQuickSearchString)
                    return;
                this.mQuickSearchString = string.Empty;
                this.mSelectedQuickIndex = 0;
                this.mSelectedQuickIndexType = (System.Type)null;
                this.Search(string.Empty, this.mQuickCategoryList);
            }
            else
                this.mFocusSearch = true;
        }

        public void SelectQuickTask(BehaviorDesignerWindow window)
        {
            if (this.mSelectedQuickIndexType == (System.Type)null)
                return;
            window.AddTask(this.mSelectedQuickIndexType, true);
        }

        public void MoveSelectedQuickTask(bool increase) => this.mSelectedQuickIndex = Mathf.Min(Mathf.Max(0, this.mSelectedQuickIndex + (!increase ? -1 : 1)), this.mQuickIndexCount);

        public void UpdateQuickTaskSearch() => this.Search(BehaviorDesignerUtility.SplitCamelCase(this.mQuickSearchString).ToLower().Replace(" ", string.Empty), this.mQuickCategoryList);

        public void DrawTaskList(BehaviorDesignerWindow window, bool enabled)
        {
            GUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());
            GUI.SetNextControlName("Search");
            string str = GUILayout.TextField(this.mSearchString, GUI.skin.FindStyle("ToolbarSeachTextField") ?? GUI.skin.FindStyle("ToolbarSearchTextField"), Array.Empty<GUILayoutOption>());
            if (this.mFocusSearch)
            {
                GUI.FocusControl("Search");
                this.mFocusSearch = false;
            }

            if (!this.mSearchString.Equals(str))
            {
                this.mSearchString = str;
                this.Search(BehaviorDesignerUtility.SplitCamelCase(this.mSearchString).ToLower().Replace(" ", string.Empty), this.mCategoryList);
            }

            GUIStyle guiStyle = !this.mSearchString.Equals(string.Empty) ? GUI.skin.FindStyle("ToolbarSeachCancelButton") ?? GUI.skin.FindStyle("ToolbarSearchCancelButton") : GUI.skin.FindStyle("ToolbarSeachCancelButtonEmpty") ?? GUI.skin.FindStyle("ToolbarSearchCancelButtonEmpty");
            if (GUILayout.Button(string.Empty, guiStyle, Array.Empty<GUILayoutOption>()))
            {
                this.mSearchString = string.Empty;
                this.Search(string.Empty, this.mCategoryList);
                GUI.FocusControl((string)null);
            }

            GUILayout.EndHorizontal();
            BehaviorDesignerUtility.DrawContentSeperator(2);
            GUILayout.Space(4f);
            this.mScrollPosition = GUILayout.BeginScrollView(this.mScrollPosition, Array.Empty<GUILayoutOption>());
            GUI.enabled = enabled;
            if (this.mCategoryList.Count > 1)
                this.DrawCategory(window, this.mCategoryList[1], false);
            if (this.mCategoryList.Count > 3)
                this.DrawCategory(window, this.mCategoryList[3], false);
            if (this.mCategoryList.Count > 0)
                this.DrawCategory(window, this.mCategoryList[0], false);
            if (this.mCategoryList.Count > 2)
                this.DrawCategory(window, this.mCategoryList[2], false);
            GUI.enabled = true;
            GUILayout.EndScrollView();
        }

        public void DrawQuickTaskList(BehaviorDesignerWindow window, bool enabled)
        {
            GUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());
            GUI.SetNextControlName("QuickSearch");
            string str = GUILayout.TextField(this.mQuickSearchString, GUI.skin.FindStyle("ToolbarSeachTextField") ?? GUI.skin.FindStyle("ToolbarSearchTextField"), Array.Empty<GUILayoutOption>());
            if (this.mFocusQuickSearch)
            {
                GUI.FocusControl("QuickSearch");
                this.mFocusQuickSearch = false;
            }

            if (!this.mQuickSearchString.Equals(str))
            {
                this.mQuickSearchString = str;
                this.mSelectedQuickIndex = 0;
                this.Search(BehaviorDesignerUtility.SplitCamelCase(this.mQuickSearchString).ToLower().Replace(" ", string.Empty), this.mQuickCategoryList);
            }

            GUIStyle guiStyle = !this.mSearchString.Equals(string.Empty) ? GUI.skin.FindStyle("ToolbarSeachCancelButton") ?? GUI.skin.FindStyle("ToolbarSearchCancelButton") : GUI.skin.FindStyle("ToolbarSeachCancelButtonEmpty") ?? GUI.skin.FindStyle("ToolbarSearchCancelButtonEmpty");
            if (GUILayout.Button(string.Empty, guiStyle, Array.Empty<GUILayoutOption>()))
            {
                this.mQuickSearchString = string.Empty;
                this.Search(string.Empty, this.mQuickCategoryList);
                GUI.FocusControl((string)null);
            }

            GUILayout.EndHorizontal();
            BehaviorDesignerUtility.DrawContentSeperator(2);
            GUILayout.Space(4f);
            this.mQuickScrollPosition = GUILayout.BeginScrollView(this.mQuickScrollPosition, false, true, Array.Empty<GUILayoutOption>());
            GUI.enabled = enabled;
            if (this.mQuickCategoryList.Count > 0)
            {
                this.mQuickIndexCount = 0;
                for (int index = 0; index < this.mQuickCategoryList.Count; ++index)
                    this.DrawCategory(window, this.mQuickCategoryList[index], true);
                if (this.mQuickIndexCount == 0)
                    GUILayout.Label("(No Tasks Found)", Array.Empty<GUILayoutOption>());
            }

            GUI.enabled = true;
            GUILayout.EndScrollView();
        }

        private void DrawCategory(
            BehaviorDesignerWindow window,
            TaskList.CategoryList category,
            bool quickList)
        {
            if (!category.Visible)
                return;
            if (!quickList)
            {
                category.Expanded = EditorGUILayout.Foldout(category.Expanded, category.Name, BehaviorDesignerUtility.TaskFoldoutGUIStyle);
                this.SetExpanded(category.ID, category.Expanded);
            }

            if (!category.Expanded)
                return;
            if (!quickList)
                ++EditorGUI.indentLevel;
            string str1 = string.Empty;
            if (category.Tasks != null)
            {
                for (int index = 0; index < category.Tasks.Count; ++index)
                {
                    if (category.Tasks[index].Visible)
                    {
                        if (quickList)
                        {
                            string str2 = category.Fullpath.TrimEnd(TaskUtility.TrimCharacters);
                            if (str1 != str2)
                            {
                                str1 = str2;
                                string[] strArray = str1.Split('/');
                                if (strArray.Length > 1)
                                {
                                    GUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());
                                    GUILayout.Space(-2f);
                                    GUILayout.Label(strArray[strArray.Length - 1], Array.Empty<GUILayoutOption>());
                                    GUILayout.EndHorizontal();
                                }
                            }
                        }

                        GUILayout.BeginHorizontal(Array.Empty<GUILayoutOption>());
                        GUILayout.Space((float)(EditorGUI.indentLevel * 16));
                        TaskNameAttribute[] taskNameAttributeArray = (TaskNameAttribute[])null;
                        if (!this.mTaskNameAttribute.TryGetValue(category.Tasks[index].Type, out taskNameAttributeArray))
                        {
                            taskNameAttributeArray = category.Tasks[index].Type.GetCustomAttributes(typeof(TaskNameAttribute), false) as TaskNameAttribute[];
                            this.mTaskNameAttribute.Add(category.Tasks[index].Type, taskNameAttributeArray);
                        }

                        string str3 = taskNameAttributeArray == null || taskNameAttributeArray.Length <= 0 ? category.Tasks[index].Name : taskNameAttributeArray[0].Name;
                        if (quickList && this.mQuickIndexCount == this.mSelectedQuickIndex)
                        {
                            GUI.backgroundColor = new Color(1f, 0.64f, 0.0f);
                            this.mSelectedQuickIndexType = category.Tasks[index].Type;
                        }

                        ++this.mQuickIndexCount;
                        if (GUILayout.Button(str3,
                                EditorStyles.miniButton,
                                new GUILayoutOption[1]
                                {
                                    GUILayout.MaxWidth((float)((!quickList ? 300 : 200) - EditorGUI.indentLevel * 16 - 24))
                                }))
                            window.AddTask(category.Tasks[index].Type, quickList);
                        GUI.backgroundColor = Color.white;
                        GUILayout.Space(3f);
                        GUILayout.EndHorizontal();
                    }
                }
            }

            if (category.Subcategories != null)
                this.DrawCategoryTaskList(window, category.Subcategories, quickList);
            if (quickList)
                return;
            --EditorGUI.indentLevel;
        }

        private void DrawCategoryTaskList(
            BehaviorDesignerWindow window,
            List<TaskList.CategoryList> categoryList,
            bool quickList)
        {
            for (int index = 0; index < categoryList.Count; ++index)
                this.DrawCategory(window, categoryList[index], quickList);
        }

        private bool Search(string searchString, List<TaskList.CategoryList> categoryList)
        {
            bool flag1 = searchString.Equals(string.Empty);
            for (int index1 = 0; index1 < categoryList.Count; ++index1)
            {
                bool flag2 = false;
                categoryList[index1].Visible = false;
                if (categoryList[index1].Subcategories != null && this.Search(searchString, categoryList[index1].Subcategories))
                {
                    categoryList[index1].Visible = true;
                    flag1 = true;
                }

                if (BehaviorDesignerUtility.SplitCamelCase(categoryList[index1].Name).ToLower().Replace(" ", string.Empty).Contains(searchString))
                {
                    flag1 = true;
                    flag2 = true;
                    categoryList[index1].Visible = true;
                    if (categoryList[index1].Subcategories != null)
                        this.MarkVisible(categoryList[index1].Subcategories);
                }

                if (categoryList[index1].Tasks != null)
                {
                    for (int index2 = 0; index2 < categoryList[index1].Tasks.Count; ++index2)
                    {
                        categoryList[index1].Tasks[index2].Visible = searchString.Equals(string.Empty);
                        if (flag2 || categoryList[index1].Tasks[index2].Name.ToLower().Replace(" ", string.Empty).Contains(searchString))
                        {
                            categoryList[index1].Tasks[index2].Visible = true;
                            flag1 = true;
                            categoryList[index1].Visible = true;
                        }
                    }
                }
            }

            return flag1;
        }

        private void MarkVisible(List<TaskList.CategoryList> categoryList)
        {
            for (int index1 = 0; index1 < categoryList.Count; ++index1)
            {
                categoryList[index1].Visible = true;
                if (categoryList[index1].Subcategories != null)
                    this.MarkVisible(categoryList[index1].Subcategories);
                if (categoryList[index1].Tasks != null)
                {
                    for (int index2 = 0; index2 < categoryList[index1].Tasks.Count; ++index2)
                        categoryList[index1].Tasks[index2].Visible = true;
                }
            }
        }

        private bool PreviouslyExpanded(int id) => EditorPrefs.GetBool(string.Format("BehaviorDesignerTaskList{0}", (object)id), true);

        private void SetExpanded(int id, bool visible) => EditorPrefs.SetBool(string.Format("BehaviorDesignerTaskList{0}", (object)id), visible);

        public enum TaskTypes
        {
            Action,
            Composite,
            Conditional,
            Decorator,
            Last,
        }

        private class SearchableType
        {
            private System.Type mType;
            private bool mVisible = true;
            private string mName;

            public SearchableType(System.Type type)
            {
                this.mType = type;
                this.mName = BehaviorDesignerUtility.SplitCamelCase(this.mType.Name);
            }

            public System.Type Type => this.mType;

            public bool Visible
            {
                get => this.mVisible;
                set => this.mVisible = value;
            }

            public string Name => this.mName;
        }

        private class CategoryList
        {
            private string mName = string.Empty;
            private string mFullpath = string.Empty;
            private List<TaskList.CategoryList> mSubcategories;
            private List<TaskList.SearchableType> mTasks;
            private bool mExpanded = true;
            private bool mVisible = true;
            private int mID;

            public CategoryList(string name, string fullpath, bool expanded, int id)
            {
                this.mName = name;
                this.mFullpath = fullpath;
                this.mExpanded = expanded;
                this.mID = id;
            }

            public string Name => this.mName;

            public string Fullpath => this.mFullpath;

            public List<TaskList.CategoryList> Subcategories => this.mSubcategories;

            public List<TaskList.SearchableType> Tasks => this.mTasks;

            public bool Expanded
            {
                get => this.mExpanded;
                set => this.mExpanded = value;
            }

            public bool Visible
            {
                get => this.mVisible;
                set => this.mVisible = value;
            }

            public int ID => this.mID;

            public void AddSubcategory(TaskList.CategoryList category)
            {
                if (this.mSubcategories == null)
                    this.mSubcategories = new List<TaskList.CategoryList>();
                this.mSubcategories.Add(category);
            }

            public void AddTask(System.Type taskType)
            {
                if (this.mTasks == null)
                    this.mTasks = new List<TaskList.SearchableType>();
                this.mTasks.Add(new TaskList.SearchableType(taskType));
            }
        }
    }
}