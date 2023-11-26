// Decompiled with JetBrains decompiler
// Type: BinaryDeserialization
// Assembly: BehaviorDesigner.Runtime, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: 4A24131E-73EC-49F7-805F-3DFB6A69FA78
// Assembly location: D:\Workspace\Reference\GameToolKit\Assets\Behavior Designer\Runtime\BehaviorDesigner.Runtime.dll

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using BehaviorDesigner.Runtime;
using BehaviorDesigner.Runtime.Tasks;
using Unity.Mathematics;
using Task = BehaviorDesigner.Runtime.Tasks.Task;
using Debug = BehaviorDesigner.Runtime.BehaviorDebug;
#if !UNITY_PLATFORM
using SerializeField = Newtonsoft.Json.JsonPropertyAttribute;
#endif

public static partial class BinaryDeserialization
{
    private static GlobalVariables globalVariables;

    private static Dictionary<BinaryDeserialization.ObjectFieldMap, List<int>> taskIDs;

    private static SHA1 shaHash;

    private static bool updatedSerialization;

    private static bool shaHashSerialization;

    private static bool strHashSerialization;

    private static int animationCurveAdvance = 20;

    private static bool enumSerialization;

    private static Dictionary<uint, string> stringCache = new Dictionary<uint, string>();

    private static byte[] sBigEndianFourByteArray;

    private static byte[] sBigEndianEightByteArray;

    private static uint[] crcTable = new uint[256]
    {
        0U,
        1996959894U,
        3993919788U,
        2567524794U,
        124634137U,
        1886057615U,
        3915621685U,
        2657392035U,
        249268274U,
        2044508324U,
        3772115230U,
        2547177864U,
        162941995U,
        2125561021U,
        3887607047U,
        2428444049U,
        498536548U,
        1789927666U,
        4089016648U,
        2227061214U,
        450548861U,
        1843258603U,
        4107580753U,
        2211677639U,
        325883990U,
        1684777152U,
        4251122042U,
        2321926636U,
        335633487U,
        1661365465U,
        4195302755U,
        2366115317U,
        997073096U,
        1281953886U,
        3579855332U,
        2724688242U,
        1006888145U,
        1258607687U,
        3524101629U,
        2768942443U,
        901097722U,
        1119000684U,
        3686517206U,
        2898065728U,
        853044451U,
        1172266101U,
        3705015759U,
        2882616665U,
        651767980U,
        1373503546U,
        3369554304U,
        3218104598U,
        565507253U,
        1454621731U,
        3485111705U,
        3099436303U,
        671266974U,
        1594198024U,
        3322730930U,
        2970347812U,
        795835527U,
        1483230225U,
        3244367275U,
        3060149565U,
        1994146192U,
        31158534U,
        2563907772U,
        4023717930U,
        1907459465U,
        112637215U,
        2680153253U,
        3904427059U,
        2013776290U,
        251722036U,
        2517215374U,
        3775830040U,
        2137656763U,
        141376813U,
        2439277719U,
        3865271297U,
        1802195444U,
        476864866U,
        2238001368U,
        4066508878U,
        1812370925U,
        453092731U,
        2181625025U,
        4111451223U,
        1706088902U,
        314042704U,
        2344532202U,
        4240017532U,
        1658658271U,
        366619977U,
        2362670323U,
        4224994405U,
        1303535960U,
        984961486U,
        2747007092U,
        3569037538U,
        1256170817U,
        1037604311U,
        2765210733U,
        3554079995U,
        1131014506U,
        879679996U,
        2909243462U,
        3663771856U,
        1141124467U,
        855842277U,
        2852801631U,
        3708648649U,
        1342533948U,
        654459306U,
        3188396048U,
        3373015174U,
        1466479909U,
        544179635U,
        3110523913U,
        3462522015U,
        1591671054U,
        702138776U,
        2966460450U,
        3352799412U,
        1504918807U,
        783551873U,
        3082640443U,
        3233442989U,
        3988292384U,
        2596254646U,
        62317068U,
        1957810842U,
        3939845945U,
        2647816111U,
        81470997U,
        1943803523U,
        3814918930U,
        2489596804U,
        225274430U,
        2053790376U,
        3826175755U,
        2466906013U,
        167816743U,
        2097651377U,
        4027552580U,
        2265490386U,
        503444072U,
        1762050814U,
        4150417245U,
        2154129355U,
        426522225U,
        1852507879U,
        4275313526U,
        2312317920U,
        282753626U,
        1742555852U,
        4189708143U,
        2394877945U,
        397917763U,
        1622183637U,
        3604390888U,
        2714866558U,
        953729732U,
        1340076626U,
        3518719985U,
        2797360999U,
        1068828381U,
        1219638859U,
        3624741850U,
        2936675148U,
        906185462U,
        1090812512U,
        3747672003U,
        2825379669U,
        829329135U,
        1181335161U,
        3412177804U,
        3160834842U,
        628085408U,
        1382605366U,
        3423369109U,
        3138078467U,
        570562233U,
        1426400815U,
        3317316542U,
        2998733608U,
        733239954U,
        1555261956U,
        3268935591U,
        3050360625U,
        752459403U,
        1541320221U,
        2607071920U,
        3965973030U,
        1969922972U,
        40735498U,
        2617837225U,
        3943577151U,
        1913087877U,
        83908371U,
        2512341634U,
        3803740692U,
        2075208622U,
        213261112U,
        2463272603U,
        3855990285U,
        2094854071U,
        198958881U,
        2262029012U,
        4057260610U,
        1759359992U,
        534414190U,
        2176718541U,
        4139329115U,
        1873836001U,
        414664567U,
        2282248934U,
        4279200368U,
        1711684554U,
        285281116U,
        2405801727U,
        4167216745U,
        1634467795U,
        376229701U,
        2685067896U,
        3608007406U,
        1308918612U,
        956543938U,
        2808555105U,
        3495958263U,
        1231636301U,
        1047427035U,
        2932959818U,
        3654703836U,
        1088359270U,
        936918000U,
        2847714899U,
        3736837829U,
        1202900863U,
        817233897U,
        3183342108U,
        3401237130U,
        1404277552U,
        615818150U,
        3134207493U,
        3453421203U,
        1423857449U,
        601450431U,
        3009837614U,
        3294710456U,
        1567103746U,
        711928724U,
        3020668471U,
        3272380065U,
        1510334235U,
        755167117U
    };

    private static byte[] BigEndianFourByteArray
    {
        get
        {
            if (BinaryDeserialization.sBigEndianFourByteArray == null)
            {
                BinaryDeserialization.sBigEndianFourByteArray = new byte[4];
            }

            return BinaryDeserialization.sBigEndianFourByteArray;
        }
        set => BinaryDeserialization.sBigEndianFourByteArray = value;
    }

    private static byte[] BigEndianEightByteArray
    {
        get
        {
            if (BinaryDeserialization.sBigEndianEightByteArray == null)
            {
                BinaryDeserialization.sBigEndianEightByteArray = new byte[8];
            }

            return BinaryDeserialization.sBigEndianEightByteArray;
        }
        set => BinaryDeserialization.sBigEndianEightByteArray = value;
    }

    public static void Load(BehaviorSource behaviorSource)
    {
        BinaryDeserialization.Load(behaviorSource.TaskData, behaviorSource, true);
    }

    public static void Load(TaskSerializationData taskData, BehaviorSource behaviorSource, bool loadTasks)
    {
        behaviorSource.EntryTask = null;
        behaviorSource.RootTask = null;
        behaviorSource.DetachedTasks = (List<Task>)null;
        behaviorSource.Variables = (List<SharedVariable>)null;
        TaskSerializationData taskSerializationData = taskData;
        if (taskSerializationData == null)
        {
            return;
        }
        FieldSerializationData serializationData = taskSerializationData.fieldSerializationData;
        if ((serializationData.byteData == null || serializationData.byteData.Count == 0)
            && (serializationData.byteDataArray == null || serializationData.byteDataArray.Length == 0))
        {
            return;
        }

        if (serializationData.byteData != null && serializationData.byteData.Count > 0)
        {
            serializationData.byteDataArray = serializationData.byteData.ToArray();
        }

        BinaryDeserialization.taskIDs = null;
        Version version = new Version(taskData.Version);
        BinaryDeserialization.updatedSerialization = version.CompareTo(new Version("1.5.7")) >= 0;
        int num1;
        BinaryDeserialization.strHashSerialization = (num1 = 0) != 0;
        BinaryDeserialization.shaHashSerialization = num1 != 0;
        BinaryDeserialization.enumSerialization = num1 != 0;
        if (BinaryDeserialization.updatedSerialization)
        {
            BinaryDeserialization.shaHashSerialization = version.CompareTo(new Version("1.5.9")) >= 0;
            if (BinaryDeserialization.shaHashSerialization)
            {
                BinaryDeserialization.strHashSerialization = version.CompareTo(new Version("1.5.11")) >= 0;
                if (BinaryDeserialization.strHashSerialization)
                {
                    BinaryDeserialization.animationCurveAdvance = version.CompareTo(new Version("1.5.12")) < 0 ? 20 : 16;
                    BinaryDeserialization.enumSerialization = version.CompareTo(new Version("1.6.4")) >= 0;
                }
            }
        }
        if (taskSerializationData.variableStartIndex != null)
        {
            List<SharedVariable> sharedVariableList = new List<SharedVariable>();
            Dictionary<int, int> fieldIndexMap = ObjectPool.Get<Dictionary<int, int>>();
            for (int index1 = 0; index1 < taskSerializationData.variableStartIndex.Count; ++index1)
            {
                int num2 = taskSerializationData.variableStartIndex[index1];
                int num3 = index1 + 1 >= taskSerializationData.variableStartIndex.Count
                        ? (
                            taskSerializationData.startIndex == null
                            || taskSerializationData.startIndex.Count <= 0
                                ? serializationData.startIndex.Count
                                : taskSerializationData.startIndex[0]
                        )
                        : taskSerializationData.variableStartIndex[index1 + 1];
                fieldIndexMap.Clear();
                for (int index2 = num2; index2 < num3; ++index2)
                {
                    fieldIndexMap.Add(
                        serializationData.fieldNameHash[index2],
                        serializationData.startIndex[index2]
                    );
                }

                SharedVariable sharedVariable = BinaryDeserialization.BytesToSharedVariable(
                    serializationData,
                    fieldIndexMap,
                    serializationData.byteDataArray,
                    taskSerializationData.variableStartIndex[index1],
                    (IVariableSource)behaviorSource,
                    false,
                    0
                );
                if (sharedVariable != null)
                {
                    sharedVariableList.Add(sharedVariable);
                }
            }
            ObjectPool.Return<Dictionary<int, int>>(fieldIndexMap);
            behaviorSource.Variables = sharedVariableList;
        }
        if (!loadTasks)
        {
            return;
        }

        List<Task> taskList = new List<Task>();
        if (taskSerializationData.types != null)
        {
            for (int index = 0; index < taskSerializationData.types.Count; ++index)
            {
                BinaryDeserialization.LoadTask(
                    taskSerializationData,
                    serializationData,
                    ref taskList,
                    ref behaviorSource
                );
            }
        }
        if (taskSerializationData.parentIndex.Count != taskList.Count)
        {
            Debug.LogError("Deserialization Error: parent index count does not match task list count");
        }
        else
        {
            for (int index3 = 0; index3 < taskSerializationData.parentIndex.Count; ++index3)
            {
                if (taskSerializationData.parentIndex[index3] == -1)
                {
                    if (behaviorSource.EntryTask == null)
                    {
                        behaviorSource.EntryTask = taskList[index3];
                    }
                    else
                    {
                        if (behaviorSource.DetachedTasks == null)
                        {
                            behaviorSource.DetachedTasks = new List<Task>();
                        }

                        behaviorSource.DetachedTasks.Add(taskList[index3]);
                    }
                }
                else if (taskSerializationData.parentIndex[index3] == 0)
                {
                    behaviorSource.RootTask = taskList[index3];
                }
                else if (
                    taskSerializationData.parentIndex[index3] != -1
                    && taskList[taskSerializationData.parentIndex[index3]] is ParentTask parentTask
                )
                {
                    int index4 = parentTask.Children != null ? parentTask.Children.Count : 0;
                    parentTask.AddChild(taskList[index3], index4);
                }
            }
            if (BinaryDeserialization.taskIDs == null)
            {
                return;
            }

            foreach (BinaryDeserialization.ObjectFieldMap key in BinaryDeserialization.taskIDs.Keys)
            {
                List<int> taskId = BinaryDeserialization.taskIDs[key];
                System.Type fieldType = key.fieldInfo.FieldType;
                if (typeof(IList).IsAssignableFrom(fieldType))
                {
                    if (fieldType.IsArray)
                    {
                        System.Type elementType = fieldType.GetElementType();
                        int length = 0;
                        for (int index = 0; index < taskId.Count; ++index)
                        {
                            Task task = taskList[taskId[index]];
                            if (elementType.IsAssignableFrom(task.GetType()))
                            {
                                ++length;
                            }
                        }
                        int index5 = 0;
                        Array instance = Array.CreateInstance(elementType, length);
                        for (int index6 = 0; index6 < instance.Length; ++index6)
                        {
                            Task task = taskList[taskId[index6]];
                            if (elementType.IsAssignableFrom(task.GetType()))
                            {
                                instance.SetValue((object)task, index5);
                                ++index5;
                            }
                        }
                        key.fieldInfo.SetValue(key.obj, (object)instance);
                    }
                    else
                    {
                        System.Type genericArgument = fieldType.GetGenericArguments()[0];
                        IList instance =
                            TaskUtility.CreateInstance(
                                typeof(List<>).MakeGenericType(genericArgument)
                            ) as IList;
                        for (int index = 0; index < taskId.Count; ++index)
                        {
                            Task task = taskList[taskId[index]];
                            if (genericArgument.IsAssignableFrom(task.GetType()))
                            {
                                instance.Add((object)task);
                            }
                        }
                        key.fieldInfo.SetValue(key.obj, (object)instance);
                    }
                }
                else if (taskList.Count > taskId[0])
                {
                    key.fieldInfo.SetValue(key.obj, (object)taskList[taskId[0]]);
                }
            }
        }
    }

    public static void Load(GlobalVariables globalVariables, string version)
    {
        if (globalVariables == null)
        {
            return;
        }

        globalVariables.Variables = (List<SharedVariable>)null;
        FieldSerializationData serializationData;
        if (
            globalVariables.VariableData == null
            || (
                (serializationData = globalVariables.VariableData.fieldSerializationData).byteData
                    == null
                || serializationData.byteData.Count == 0
            )
                && (
                    serializationData.byteDataArray == null
                    || serializationData.byteDataArray.Length == 0
                )
        )
        {
            return;
        }

        VariableSerializationData variableData = globalVariables.VariableData;
        if (serializationData.byteData != null && serializationData.byteData.Count > 0)
        {
            serializationData.byteDataArray = serializationData.byteData.ToArray();
        }

        Version version1 = new Version(globalVariables.Version);
        BinaryDeserialization.updatedSerialization = version1.CompareTo(new Version("1.5.7")) >= 0;
        int num1;
        BinaryDeserialization.strHashSerialization = (num1 = 0) != 0;
        BinaryDeserialization.shaHashSerialization = num1 != 0;
        BinaryDeserialization.enumSerialization = num1 != 0;
        if (BinaryDeserialization.updatedSerialization)
        {
            BinaryDeserialization.shaHashSerialization =
                version1.CompareTo(new Version("1.5.9")) >= 0;
            if (BinaryDeserialization.shaHashSerialization)
            {
                BinaryDeserialization.strHashSerialization =
                    version1.CompareTo(new Version("1.5.11")) >= 0;
                if (BinaryDeserialization.strHashSerialization)
                {
                    BinaryDeserialization.animationCurveAdvance =
                        version1.CompareTo(new Version("1.5.12")) < 0 ? 20 : 16;
                    BinaryDeserialization.enumSerialization =
                        version1.CompareTo(new Version("1.6.4")) >= 0;
                }
            }
        }
        if (variableData.variableStartIndex == null)
        {
            return;
        }

        List<SharedVariable> sharedVariableList = new List<SharedVariable>();
        Dictionary<int, int> fieldIndexMap = ObjectPool.Get<Dictionary<int, int>>();
        for (int index1 = 0; index1 < variableData.variableStartIndex.Count; ++index1)
        {
            int num2 = variableData.variableStartIndex[index1];
            int num3 =
                index1 + 1 >= variableData.variableStartIndex.Count
                    ? serializationData.startIndex.Count
                    : variableData.variableStartIndex[index1 + 1];
            fieldIndexMap.Clear();
            for (int index2 = num2; index2 < num3; ++index2)
            {
                fieldIndexMap.Add(
                    serializationData.fieldNameHash[index2],
                    serializationData.startIndex[index2]
                );
            }

            SharedVariable sharedVariable = BinaryDeserialization.BytesToSharedVariable(
                serializationData,
                fieldIndexMap,
                serializationData.byteDataArray,
                variableData.variableStartIndex[index1],
                (IVariableSource)globalVariables,
                false,
                0
            );
            if (sharedVariable != null)
            {
                sharedVariableList.Add(sharedVariable);
            }
        }
        ObjectPool.Return<Dictionary<int, int>>(fieldIndexMap);
        globalVariables.Variables = sharedVariableList;
    }

    public static void LoadTask(
        TaskSerializationData taskSerializationData,
        FieldSerializationData fieldSerializationData,
        ref List<Task> taskList,
        ref BehaviorSource behaviorSource
    )
    {
        int count = taskList.Count;
        int index1 = taskSerializationData.startIndex[count];
        int num1 =
            count + 1 >= taskSerializationData.startIndex.Count
                ? fieldSerializationData.startIndex.Count
                : taskSerializationData.startIndex[count + 1];
        Dictionary<int, int> fieldIndexMap = ObjectPool.Get<Dictionary<int, int>>();
        fieldIndexMap.Clear();
        for (int index2 = index1; index2 < num1; ++index2)
        {
            if (!fieldIndexMap.ContainsKey(fieldSerializationData.fieldNameHash[index2]))
            {
                fieldIndexMap.Add(
                    fieldSerializationData.fieldNameHash[index2],
                    fieldSerializationData.startIndex[index2]
                );
            }
        }
        System.Type t = TaskUtility.GetTypeWithinAssembly(taskSerializationData.types[count]);
        if (t == (System.Type)null)
        {
            bool flag = false;
            for (int index3 = 0; index3 < taskSerializationData.parentIndex.Count; ++index3)
            {
                if (count == taskSerializationData.parentIndex[index3])
                {
                    flag = true;
                    break;
                }
            }
            t = !flag ? typeof(UnknownTask) : typeof(UnknownParentTask);
        }
        Task instance = TaskUtility.CreateInstance(t) as Task;
        if (instance is UnknownTask)
        {
            UnknownTask unknownTask = instance as UnknownTask;
            for (int index4 = index1; index4 < num1; ++index4)
            {
                unknownTask.fieldNameHash.Add(fieldSerializationData.fieldNameHash[index4]);
                unknownTask
                    .startIndex
                    .Add(
                        fieldSerializationData.startIndex[index4]
                            - fieldSerializationData.startIndex[index1]
                    );
            }
            for (
                int index5 = fieldSerializationData.startIndex[index1];
                index5 <= fieldSerializationData.startIndex[num1 - 1];
                ++index5
            )
            {
                unknownTask
                    .dataPosition
                    .Add(
                        fieldSerializationData.dataPosition[index5]
                            - fieldSerializationData.dataPosition[
                                fieldSerializationData.startIndex[index1]
                            ]
                    );
            }

            int num2 =
                count + 1 >= taskSerializationData.startIndex.Count
                || taskSerializationData.startIndex[count + 1]
                    >= fieldSerializationData.dataPosition.Count
                    ? fieldSerializationData.byteDataArray.Length
                    : fieldSerializationData.dataPosition[
                        taskSerializationData.startIndex[count + 1]
                    ];
            for (
                int index6 = fieldSerializationData.dataPosition[
                    fieldSerializationData.startIndex[index1]
                ];
                index6 < num2;
                ++index6
            )
            {
                unknownTask.byteData.Add(fieldSerializationData.byteDataArray[index6]);
            }

            unknownTask.unityObjects = fieldSerializationData.unityObjects;
        }
        instance.Owner = behaviorSource.Owner.GetObject() as Behavior;
        taskList.Add(instance);
        instance.ID = (int)
            BinaryDeserialization.LoadField(
                fieldSerializationData,
                fieldIndexMap,
                typeof(int),
                "ID",
                0,
                (IVariableSource)null
            );
        instance.FriendlyName =
            BinaryDeserialization.LoadField(
                fieldSerializationData,
                fieldIndexMap,
                typeof(string),
                "FriendlyName",
                0,
                (IVariableSource)null
            ) as string;
        instance.IsInstant = (bool)
            BinaryDeserialization.LoadField(
                fieldSerializationData,
                fieldIndexMap,
                typeof(bool),
                "IsInstant",
                0,
                (IVariableSource)null
            );
        object obj;
        if (
            (
                obj = BinaryDeserialization.LoadField(
                    fieldSerializationData,
                    fieldIndexMap,
                    typeof(bool),
                    "Disabled",
                    0,
                    (IVariableSource)null
                )
            ) != null
        )
        {
            instance.Disabled = (bool)obj;
        }

        BinaryDeserialization.LoadNodeData(fieldSerializationData, fieldIndexMap, taskList[count]);
        if (
            instance.GetType().Equals(typeof(UnknownTask))
            || instance.GetType().Equals(typeof(UnknownParentTask))
        )
        {
            if (!instance.FriendlyName.Contains("Unknown "))
            {
                instance.FriendlyName = string.Format("Unknown {0}", (object)instance.FriendlyName);
            }

            instance.NodeData.Comment = "Unknown Task. Right click and Replace to locate new task.";
        }
        BinaryDeserialization.LoadFields(
            fieldSerializationData,
            fieldIndexMap,
            (object)taskList[count],
            0,
            (IVariableSource)behaviorSource
        );
        ObjectPool.Return<Dictionary<int, int>>(fieldIndexMap);
    }

    private static void LoadNodeData(
        FieldSerializationData fieldSerializationData,
        Dictionary<int, int> fieldIndexMap,
        Task task
    )
    {
        NodeData nodeData = new NodeData();
        nodeData.Offset = (float2)
            BinaryDeserialization.LoadField(
                fieldSerializationData,
                fieldIndexMap,
                typeof(float2),
                "NodeDataOffset",
                0,
                (IVariableSource)null
            );
        nodeData.Comment =
            BinaryDeserialization.LoadField(
                fieldSerializationData,
                fieldIndexMap,
                typeof(string),
                "NodeDataComment",
                0,
                (IVariableSource)null
            ) as string;
        nodeData.IsBreakpoint = (bool)
            BinaryDeserialization.LoadField(
                fieldSerializationData,
                fieldIndexMap,
                typeof(bool),
                "NodeDataIsBreakpoint",
                0,
                (IVariableSource)null
            );
        nodeData.Collapsed = (bool)
            BinaryDeserialization.LoadField(
                fieldSerializationData,
                fieldIndexMap,
                typeof(bool),
                "NodeDataCollapsed",
                0,
                (IVariableSource)null
            );
        object obj1 = BinaryDeserialization.LoadField(
            fieldSerializationData,
            fieldIndexMap,
            typeof(int),
            "NodeDataColorIndex",
            0,
            (IVariableSource)null
        );
        if (obj1 != null)
        {
            nodeData.ColorIndex = (int)obj1;
        }

        object obj2 = BinaryDeserialization.LoadField(
            fieldSerializationData,
            fieldIndexMap,
            typeof(List<string>),
            "NodeDataWatchedFields",
            0,
            (IVariableSource)null
        );
        if (obj2 != null)
        {
            nodeData.WatchedFieldNames = new List<string>();
            nodeData.WatchedFields = new List<FieldInfo>();
            IList list = obj2 as IList;
            for (int index = 0; index < list.Count; ++index)
            {
                FieldInfo field = task.GetType()
                    .GetField(
                        (string)list[index],
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
                    );
                if (field != (FieldInfo)null)
                {
                    nodeData.WatchedFieldNames.Add(field.Name);
                    nodeData.WatchedFields.Add(field);
                }
            }
        }
        task.NodeData = nodeData;
    }

#if !UNITY_PLATFORM
    private static void LoadFields(FieldSerializationData fieldSerializationData, Dictionary<int, int> fieldIndexMap, object obj, int hashPrefix, IVariableSource variableSource)
    {
        FieldInfo[] serializableFields = TaskUtility.GetSerializableFields(obj.GetType());
        for (int index = 0; index < serializableFields.Length; ++index)
        {
            if (!TaskUtility.HasAttribute(serializableFields[index], typeof(NonSerializedAttribute))
                && (!serializableFields[index].IsPrivate && !serializableFields[index].IsFamily
                    || TaskUtility.HasAttribute(serializableFields[index], typeof(SerializeField)))
                && (!(obj is ParentTask) || !serializableFields[index].Name.Equals("children")))
            {
                object objA = BinaryDeserialization.LoadField(
                    fieldSerializationData,
                    fieldIndexMap,
                    serializableFields[index].FieldType,
                    serializableFields[index].Name,
                    hashPrefix,
                    variableSource,
                    obj,
                    serializableFields[index]
                );
                if (
                    objA != null
                    && !object.ReferenceEquals(objA, (object)null)
                    && !objA.Equals((object)null)
                    && serializableFields[index].FieldType.IsAssignableFrom(objA.GetType())
                )
                {
                    serializableFields[index].SetValue(obj, objA);
                }
            }
        }
    }
#endif

#if !UNITY_PLATFORM
    private static object LoadField(
        FieldSerializationData fieldSerializationData, Dictionary<int, int> fieldIndexMap, System.Type fieldType, string fieldName, int hashPrefix, IVariableSource variableSource, object obj = null, FieldInfo fieldInfo = null)
    {
        int num1 = hashPrefix;
        int num2 = !BinaryDeserialization.shaHashSerialization
            ? num1 + (fieldType.Name.GetHashCode() + fieldName.GetHashCode())
            : num1
                + (
                    BinaryDeserialization.StringHash(fieldType.Name.ToString(), BinaryDeserialization.strHashSerialization)
                    + BinaryDeserialization.StringHash(fieldName, BinaryDeserialization.strHashSerialization)
                );
        int num3;
        bool flag = fieldIndexMap.TryGetValue(num2, out num3);
        // if (
        //     !flag
        //     && fieldInfo != (FieldInfo)null
        //     && fieldInfo.GetCustomAttribute(typeof(FormerlySerializedAsAttribute), true)
        //         is FormerlySerializedAsAttribute customAttribute
        // )
        // {
        //     num2 =
        //         hashPrefix
        //         + BinaryDeserialization.StringHash(
        //             fieldType.Name.ToString(),
        //             BinaryDeserialization.strHashSerialization
        //         )
        //         + BinaryDeserialization.StringHash(
        //             customAttribute.oldName,
        //             BinaryDeserialization.strHashSerialization
        //         );
        //     flag = fieldIndexMap.TryGetValue(num2, out num3);
        // }
        if (!flag)
        {
            if (fieldType.IsAbstract)
            {
                return (object)null;
            }

            if (!typeof(SharedVariable).IsAssignableFrom(fieldType))
            {
                return (object)null;
            }

            SharedVariable instance = TaskUtility.CreateInstance(fieldType) as SharedVariable;
            if (fieldInfo.GetValue(obj) is SharedVariable sharedVariable)
            {
                instance.SetValue(sharedVariable.GetValue());
            }

            return (object)instance;
        }
        object obj1 = (object)null;
        if (typeof(IList).IsAssignableFrom(fieldType))
        {
            int length = BinaryDeserialization.BytesToInt(fieldSerializationData.byteDataArray, fieldSerializationData.dataPosition[num3]);
            if (fieldType.IsArray)
            {
                System.Type elementType = fieldType.GetElementType();
                if (elementType == (System.Type)null)
                {
                    return (object)null;
                }

                Array instance = Array.CreateInstance(elementType, length);
                for (int index = 0; index < length; ++index)
                {
                    object objA = BinaryDeserialization.LoadField(fieldSerializationData, fieldIndexMap, elementType, index.ToString(), num2 / (!BinaryDeserialization.updatedSerialization ? 1 : index + 1), variableSource, obj, fieldInfo);
                    instance.SetValue(
                        object.ReferenceEquals(
                            objA, (object)null) || objA.Equals((object)null)
                            ? (object)null
                            : objA,
                        index
                    );
                }
                obj1 = (object)instance;
            }
            else
            {
                System.Type type = fieldType;
                while (!type.IsGenericType)
                {
                    type = type.BaseType;
                }

                System.Type genericArgument = type.GetGenericArguments()[0];
                IList instance;
                if (fieldType.IsGenericType)
                {
                    instance =
                        TaskUtility.CreateInstance(typeof(List<>).MakeGenericType(genericArgument))
                        as IList;
                }
                else
                {
                    instance = TaskUtility.CreateInstance(fieldType) as IList;
                }

                for (int index = 0; index < length; ++index)
                {
                    object objA = BinaryDeserialization.LoadField(fieldSerializationData, fieldIndexMap, genericArgument, index.ToString(), num2 / (!BinaryDeserialization.updatedSerialization ? 1 : index + 1), variableSource, obj, fieldInfo);
                    instance.Add(
                        object.ReferenceEquals(objA, (object)null) || objA.Equals((object)null)
                            ? (object)null
                            : objA
                    );
                }
                obj1 = (object)instance;
            }
        }
        else if (typeof(Task).IsAssignableFrom(fieldType))
        {
            if (
                fieldInfo != (FieldInfo)null
                && TaskUtility.HasAttribute(fieldInfo, typeof(InspectTaskAttribute))
            )
            {
                string typeName = BinaryDeserialization.BytesToString(
                    fieldSerializationData.byteDataArray,
                    fieldSerializationData.dataPosition[num3],
                    BinaryDeserialization.GetFieldSize(fieldSerializationData, num3)
                );
                if (!string.IsNullOrEmpty(typeName))
                {
                    System.Type typeWithinAssembly = TaskUtility.GetTypeWithinAssembly(typeName);
                    if (typeWithinAssembly != (System.Type)null)
                    {
                        obj1 = TaskUtility.CreateInstance(typeWithinAssembly);
                        BinaryDeserialization.LoadFields(fieldSerializationData, fieldIndexMap, obj1, num2, variableSource);
                    }
                }
            }
            else
            {
                if (BinaryDeserialization.taskIDs == null)
                {
                    BinaryDeserialization.taskIDs = new Dictionary<
                        BinaryDeserialization.ObjectFieldMap,
                        List<int>
                    >(
                        (IEqualityComparer<BinaryDeserialization.ObjectFieldMap>)
                            new BinaryDeserialization.ObjectFieldMapComparer()
                    );
                }

                int num4 = BinaryDeserialization.BytesToInt(
                    fieldSerializationData.byteDataArray,
                    fieldSerializationData.dataPosition[num3]
                );
                BinaryDeserialization.ObjectFieldMap key = new BinaryDeserialization.ObjectFieldMap(
                    obj,
                    fieldInfo
                );
                if (BinaryDeserialization.taskIDs.ContainsKey(key))
                {
                    BinaryDeserialization.taskIDs[key].Add(num4);
                }
                else
                {
                    BinaryDeserialization.taskIDs.Add(key, new List<int>() { num4 });
                }
            }
        }
        else if (typeof(SharedVariable).IsAssignableFrom(fieldType))
        {
            obj1 = (object)
                BinaryDeserialization.BytesToSharedVariable(fieldSerializationData, fieldIndexMap, fieldSerializationData.byteDataArray, fieldSerializationData.dataPosition[num3], variableSource, true, num2);
        }
        else if (typeof(System.Object).IsAssignableFrom(fieldType))
        {
            obj1 = (object)
                BinaryDeserialization.IndexToUnityObject(
                    BinaryDeserialization.BytesToInt(
                        fieldSerializationData.byteDataArray,
                        fieldSerializationData.dataPosition[num3]
                    ),
                    fieldSerializationData
                );
        }
        else if (
            fieldType.Equals(typeof(int))
            || !BinaryDeserialization.enumSerialization && fieldType.IsEnum
        )
        {
            obj1 = (object)
                BinaryDeserialization.BytesToInt(
                    fieldSerializationData.byteDataArray,
                    fieldSerializationData.dataPosition[num3]
                );
            if (fieldType.IsEnum)
            {
                obj1 = Enum.ToObject(fieldType, obj1);
            }
        }
        else if (fieldType.IsEnum)
        {
            obj1 = Enum.ToObject(
                fieldType,
                BinaryDeserialization.LoadField(fieldSerializationData, fieldIndexMap, Enum.GetUnderlyingType(fieldType), fieldName, num2, variableSource, obj, fieldInfo)
            );
        }
        else if (fieldType.Equals(typeof(uint)))
        {
            obj1 = (object)
                BinaryDeserialization.BytesToUInt(
                    fieldSerializationData.byteDataArray,
                    fieldSerializationData.dataPosition[num3]
                );
        }
        else if (fieldType.Equals(typeof(ulong)) || fieldType.Equals(typeof(ulong)))
        {
            obj1 = (object)
                BinaryDeserialization.BytesToULong(
                    fieldSerializationData.byteDataArray,
                    fieldSerializationData.dataPosition[num3]
                );
        }
        else if (fieldType.Equals(typeof(ushort)))
        {
            obj1 = (object)
                BinaryDeserialization.BytesToUShort(
                    fieldSerializationData.byteDataArray,
                    fieldSerializationData.dataPosition[num3]
                );
        }
        else if (fieldType.Equals(typeof(float)))
        {
            obj1 = (object)
                BinaryDeserialization.BytesToFloat(
                    fieldSerializationData.byteDataArray,
                    fieldSerializationData.dataPosition[num3]
                );
        }
        else if (fieldType.Equals(typeof(double)))
        {
            obj1 = (object)
                BinaryDeserialization.BytesToDouble(
                    fieldSerializationData.byteDataArray,
                    fieldSerializationData.dataPosition[num3]
                );
        }
        else if (fieldType.Equals(typeof(long)))
        {
            obj1 = (object)
                BinaryDeserialization.BytesToLong(
                    fieldSerializationData.byteDataArray,
                    fieldSerializationData.dataPosition[num3]
                );
        }
        else if (fieldType.Equals(typeof(ulong)))
        {
            obj1 = (object)
                BinaryDeserialization.BytesToULong(
                    fieldSerializationData.byteDataArray,
                    fieldSerializationData.dataPosition[num3]
                );
        }
        else if (fieldType.Equals(typeof(bool)))
        {
            obj1 = (object)
                BinaryDeserialization.BytesToBool(
                    fieldSerializationData.byteDataArray,
                    fieldSerializationData.dataPosition[num3]
                );
        }
        else if (fieldType.Equals(typeof(string)))
        {
            obj1 = (object)
                BinaryDeserialization.BytesToString(
                    fieldSerializationData.byteDataArray,
                    fieldSerializationData.dataPosition[num3],
                    BinaryDeserialization.GetFieldSize(fieldSerializationData, num3)
                );
        }
        else if (fieldType.Equals(typeof(byte)))
        {
            obj1 = (object)
                BinaryDeserialization.BytesToByte(
                    fieldSerializationData.byteDataArray,
                    fieldSerializationData.dataPosition[num3]
                );
        }
        else if (fieldType.Equals(typeof(float2)))
        {
            obj1 = (object)
                BinaryDeserialization.BytesToVector2(
                    fieldSerializationData.byteDataArray,
                    fieldSerializationData.dataPosition[num3]
                );
        }
        // else if (fieldType.Equals(typeof(Vector2Int)))
        // {
        //     obj1 = (object)
        //         BinaryDeserialization.BytesToVector2Int(
        //             fieldSerializationData.byteDataArray,
        //             fieldSerializationData.dataPosition[num3]
        //         );
        // }
        else if (fieldType.Equals(typeof(float3)))
        {
            obj1 = (object)
                BinaryDeserialization.BytesToVector3(
                    fieldSerializationData.byteDataArray,
                    fieldSerializationData.dataPosition[num3]
                );
        }
        // else if (fieldType.Equals(typeof(Vector3Int)))
        // {
        //     obj1 = (object)
        //         BinaryDeserialization.BytesToVector3Int(
        //             fieldSerializationData.byteDataArray,
        //             fieldSerializationData.dataPosition[num3]
        //         );
        // }
        else if (fieldType.Equals(typeof(float4)))
        {
            obj1 = (object)
                BinaryDeserialization.BytesToVector4(
                    fieldSerializationData.byteDataArray,
                    fieldSerializationData.dataPosition[num3]
                );
        }
        else if (fieldType.Equals(typeof(quaternion)))
        {
            obj1 = (object)
                BinaryDeserialization.BytesToQuaternion(
                    fieldSerializationData.byteDataArray,
                    fieldSerializationData.dataPosition[num3]
                );
        }
        // else if (fieldType.Equals(typeof(Color)))
        // {
        //     obj1 = (object)
        //         BinaryDeserialization.BytesToColor(
        //             fieldSerializationData.byteDataArray,
        //             fieldSerializationData.dataPosition[num3]
        //         );
        // }
        // else if (fieldType.Equals(typeof(Rect)))
        // {
        //     obj1 = (object)
        //         BinaryDeserialization.BytesToRect(
        //             fieldSerializationData.byteDataArray,
        //             fieldSerializationData.dataPosition[num3]
        //         );
        // }
        else if (fieldType.Equals(typeof(float4x4)))
        {
            obj1 = (object)
                BinaryDeserialization.BytesToMatrix4x4(
                    fieldSerializationData.byteDataArray,
                    fieldSerializationData.dataPosition[num3]
                );
        }
        // else if (fieldType.Equals(typeof(AnimationCurve)))
        // {
        //     obj1 = (object)
        //         BinaryDeserialization.BytesToAnimationCurve(
        //             fieldSerializationData.byteDataArray,
        //             fieldSerializationData.dataPosition[num3]
        //         );
        // }
        // else if (fieldType.Equals(typeof(LayerMask)))
        // {
        //     obj1 = (object)
        //         BinaryDeserialization.BytesToLayerMask(
        //             fieldSerializationData.byteDataArray,
        //             fieldSerializationData.dataPosition[num3]
        //         );
        // }
        else if (fieldType.IsClass || fieldType.IsValueType && !fieldType.IsPrimitive)
        {
            object instance = TaskUtility.CreateInstance(fieldType);
            BinaryDeserialization.LoadFields(
                fieldSerializationData,
                fieldIndexMap,
                instance,
                num2,
                variableSource
            );
            return instance;
        }
        return obj1;
    }
#endif

    public static int StringHash(string value, bool fastHash)
    {
        if (string.IsNullOrEmpty(value))
        {
            return 0;
        }

        if (fastHash)
        {
            int num = 23;
            int length = value.Length;
            for (int index = 0; index < length; ++index)
            {
                num = num * 31 + (int)value[index];
            }

            return num;
        }
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        if (BinaryDeserialization.shaHash == null)
        {
            BinaryDeserialization.shaHash = (SHA1)new SHA1Managed();
        }

        return BitConverter.ToInt32(BinaryDeserialization.shaHash.ComputeHash(bytes), 0);
    }

    private static int GetFieldSize(FieldSerializationData fieldSerializationData, int fieldIndex)
    {
        return (
            fieldIndex + 1 >= fieldSerializationData.dataPosition.Count
                ? fieldSerializationData.byteDataArray.Length
                : fieldSerializationData.dataPosition[fieldIndex + 1]
        ) - fieldSerializationData.dataPosition[fieldIndex];
    }


    private static int BytesToInt(byte[] bytes, int dataPosition)
    {
        if (BitConverter.IsLittleEndian)
        {
            return BitConverter.ToInt32(bytes, dataPosition);
        }

        Array.Copy((Array)bytes, dataPosition, (Array)BinaryDeserialization.BigEndianFourByteArray, 0, 4);
        Array.Reverse<byte>(BinaryDeserialization.BigEndianFourByteArray);
        return BitConverter.ToInt32(BinaryDeserialization.BigEndianFourByteArray, 0);
    }

    private static uint BytesToUInt(byte[] bytes, int dataPosition)
    {
        if (BitConverter.IsLittleEndian)
        {
            return BitConverter.ToUInt32(bytes, dataPosition);
        }

        Array.Copy((Array)bytes, dataPosition, (Array)BinaryDeserialization.BigEndianFourByteArray, 0, 4);
        Array.Reverse<byte>(BinaryDeserialization.BigEndianFourByteArray);
        return BitConverter.ToUInt32(BinaryDeserialization.BigEndianFourByteArray, 0);
    }

    private static ulong BytesToULong(byte[] bytes, int dataPosition)
    {
        if (BitConverter.IsLittleEndian)
        {
            return BitConverter.ToUInt64(bytes, dataPosition);
        }

        Array.Copy((Array)bytes, dataPosition, (Array)BinaryDeserialization.BigEndianEightByteArray, 0, 8);
        Array.Reverse<byte>(BinaryDeserialization.BigEndianEightByteArray);
        return BitConverter.ToUInt64(BinaryDeserialization.BigEndianEightByteArray, 0);
    }

    private static ushort BytesToUShort(byte[] bytes, int dataPosition)
    {
        if (BitConverter.IsLittleEndian)
        {
            return BitConverter.ToUInt16(bytes, dataPosition);
        }

        Array.Copy((Array)bytes, dataPosition, (Array)BinaryDeserialization.BigEndianFourByteArray, 0, 4);
        Array.Reverse<byte>(BinaryDeserialization.BigEndianFourByteArray);
        return BitConverter.ToUInt16(BinaryDeserialization.BigEndianFourByteArray, 0);
    }

    private static float BytesToFloat(byte[] bytes, int dataPosition)
    {
        if (BitConverter.IsLittleEndian)
        {
            return BitConverter.ToSingle(bytes, dataPosition);
        }

        Array.Copy((Array)bytes, dataPosition, (Array)BinaryDeserialization.BigEndianFourByteArray, 0, 4);
        Array.Reverse<byte>(BinaryDeserialization.BigEndianFourByteArray);
        return BitConverter.ToSingle(BinaryDeserialization.BigEndianFourByteArray, 0);
    }

    private static double BytesToDouble(byte[] bytes, int dataPosition)
    {
        if (BitConverter.IsLittleEndian)
        {
            return BitConverter.ToDouble(bytes, dataPosition);
        }

        Array.Copy((Array)bytes, dataPosition, (Array)BinaryDeserialization.BigEndianEightByteArray, 0, 8);
        Array.Reverse<byte>(BinaryDeserialization.BigEndianEightByteArray);
        return BitConverter.ToDouble(BinaryDeserialization.BigEndianEightByteArray, 0);
    }

    private static long BytesToLong(byte[] bytes, int dataPosition)
    {
        if (BitConverter.IsLittleEndian)
        {
            return BitConverter.ToInt64(bytes, dataPosition);
        }

        Array.Copy((Array)bytes, dataPosition, (Array)BinaryDeserialization.BigEndianEightByteArray, 0, 8);
        Array.Reverse<byte>(BinaryDeserialization.BigEndianEightByteArray);
        return BitConverter.ToInt64(BinaryDeserialization.BigEndianEightByteArray, 0);
    }

    private static bool BytesToBool(byte[] bytes, int dataPosition) =>
        BitConverter.ToBoolean(bytes, dataPosition);

    private static string BytesToString(byte[] bytes, int dataPosition, int dataSize)
    {
        if (dataSize == 0)
        {
            return string.Empty;
        }

        uint key = BinaryDeserialization.crc32(bytes, dataPosition, dataSize);
        string str;
        if (!BinaryDeserialization.stringCache.TryGetValue(key, out str))
        {
            str = Encoding.UTF8.GetString(bytes, dataPosition, dataSize);
            BinaryDeserialization.stringCache.Add(key, str);
        }
        return str;
    }

    public static uint crc32(byte[] input, int dataPosition, int dataSize)
    {
        uint num1 = uint.MaxValue;
        int length = input.Length;
        for (int index = dataPosition; index < dataPosition + dataSize; ++index)
        {
            num1 =
                num1 >> 8
                ^ BinaryDeserialization.crcTable[
                    (int)(uint)(((int)num1 ^ (int)input[index]) & (int)byte.MaxValue)
                ];
        }

        uint num2 = (uint)((ulong)num1 ^ ulong.MaxValue);
        if (num2 < 0U)
        {
            num2 = num2;
        }

        return num2;
    }

    private static byte BytesToByte(byte[] bytes, int dataPosition)
    {
        return bytes[dataPosition];
    }

    private static float2 BytesToVector2(byte[] bytes, int dataPosition)
    {
        return new float2()
        {
            x = BinaryDeserialization.BytesToFloat(bytes, dataPosition),
            y = BinaryDeserialization.BytesToFloat(bytes, dataPosition + 4)
        };
    }

    private static float3 BytesToVector3(byte[] bytes, int dataPosition)
    {
        return new float3()
        {
            x = BinaryDeserialization.BytesToFloat(bytes, dataPosition),
            y = BinaryDeserialization.BytesToFloat(bytes, dataPosition + 4),
            z = BinaryDeserialization.BytesToFloat(bytes, dataPosition + 8)
        };
    }

    private static float4 BytesToVector4(byte[] bytes, int dataPosition)
    {
        return new float4()
        {
            x = BinaryDeserialization.BytesToFloat(bytes, dataPosition),
            y = BinaryDeserialization.BytesToFloat(bytes, dataPosition + 4),
            z = BinaryDeserialization.BytesToFloat(bytes, dataPosition + 8),
            w = BinaryDeserialization.BytesToFloat(bytes, dataPosition + 12)
        };
    }

    private static quaternion BytesToQuaternion(byte[] bytes, int dataPosition)
    {
        return new quaternion(
            BinaryDeserialization.BytesToFloat(bytes, dataPosition),
            BinaryDeserialization.BytesToFloat(bytes, dataPosition + 4),
            BinaryDeserialization.BytesToFloat(bytes, dataPosition + 8),
            BinaryDeserialization.BytesToFloat(bytes, dataPosition + 12)
        );
    }

    private static float4x4 BytesToMatrix4x4(byte[] bytes, int dataPosition)
    {
        return new float4x4
        (
            BinaryDeserialization.BytesToFloat(bytes, dataPosition),
            BinaryDeserialization.BytesToFloat(bytes, dataPosition + 4),
            BinaryDeserialization.BytesToFloat(bytes, dataPosition + 8),
            BinaryDeserialization.BytesToFloat(bytes, dataPosition + 12),
            BinaryDeserialization.BytesToFloat(bytes, dataPosition + 16),
            BinaryDeserialization.BytesToFloat(bytes, dataPosition + 20),
            BinaryDeserialization.BytesToFloat(bytes, dataPosition + 24),
            BinaryDeserialization.BytesToFloat(bytes, dataPosition + 28),
            BinaryDeserialization.BytesToFloat(bytes, dataPosition + 32),
            BinaryDeserialization.BytesToFloat(bytes, dataPosition + 36),
            BinaryDeserialization.BytesToFloat(bytes, dataPosition + 40),
            BinaryDeserialization.BytesToFloat(bytes, dataPosition + 44),
            BinaryDeserialization.BytesToFloat(bytes, dataPosition + 48),
            BinaryDeserialization.BytesToFloat(bytes, dataPosition + 52),
            BinaryDeserialization.BytesToFloat(bytes, dataPosition + 56),
            BinaryDeserialization.BytesToFloat(bytes, dataPosition + 60)
        );
    }

#if !UNITY_PLATFORM
    private static System.Object IndexToUnityObject(int index, FieldSerializationData activeFieldSerializationData)
    {
        return index < 0 || index >= activeFieldSerializationData.unityObjects.Count
            ? null
            : activeFieldSerializationData.unityObjects[index];
    }
#endif

#if !UNITY_PLATFORM
    private static SharedVariable BytesToSharedVariable(
        FieldSerializationData fieldSerializationData, Dictionary<int, int> fieldIndexMap, byte[] bytes, int dataPosition, IVariableSource variableSource, bool fromField, int hashPrefix)
    {
        SharedVariable sharedVariable = (SharedVariable)null;
        string typeName =
            BinaryDeserialization.LoadField(
                fieldSerializationData, fieldIndexMap, typeof(string), "Type", hashPrefix, (IVariableSource)null) as string;
        if (string.IsNullOrEmpty(typeName))
        {
            return (SharedVariable)null;
        }

        string name =
            BinaryDeserialization.LoadField(fieldSerializationData, fieldIndexMap, typeof(string), "Name", hashPrefix, (IVariableSource)null) as string;
        bool boolean1 = Convert.ToBoolean(
            BinaryDeserialization.LoadField(fieldSerializationData, fieldIndexMap, typeof(bool), "IsShared", hashPrefix, (IVariableSource)null)
        );
        bool boolean2 = Convert.ToBoolean(
            BinaryDeserialization.LoadField(fieldSerializationData, fieldIndexMap, typeof(bool), "IsGlobal", hashPrefix, (IVariableSource)null)
        );
        bool boolean3 = Convert.ToBoolean(
            BinaryDeserialization.LoadField(fieldSerializationData, fieldIndexMap, typeof(bool), "IsDynamic", hashPrefix, (IVariableSource)null)
        );
        if (boolean1 && (!boolean3 || BehaviorManager.IsPlaying) && fromField)
        {
            if (!boolean2)
            {
                sharedVariable = variableSource.GetVariable(name);
            }
            else
            {
                if (BinaryDeserialization.globalVariables == null)
                {
                    BinaryDeserialization.globalVariables = GlobalVariables.Instance;
                }

                if (BinaryDeserialization.globalVariables != null)
                {
                    sharedVariable = BinaryDeserialization.globalVariables.GetVariable(name);
                }
            }
        }
        System.Type typeWithinAssembly = TaskUtility.GetTypeWithinAssembly(typeName);
        if (typeWithinAssembly == (System.Type)null)
        {
            return (SharedVariable)null;
        }

        bool flag = true;
        if (sharedVariable == null || !(flag = sharedVariable.GetType().Equals(typeWithinAssembly)))
        {
            sharedVariable = TaskUtility.CreateInstance(typeWithinAssembly) as SharedVariable;
            sharedVariable.Name = name;
            sharedVariable.IsShared = boolean1;
            sharedVariable.IsGlobal = boolean2;
            sharedVariable.IsDynamic = boolean3;
            sharedVariable.Tooltip =
                BinaryDeserialization.LoadField(fieldSerializationData, fieldIndexMap, typeof(string), "Tooltip", hashPrefix, (IVariableSource)null) as string;
            if (!boolean2)
            {
                sharedVariable.PropertyMapping =
                    BinaryDeserialization.LoadField(fieldSerializationData, fieldIndexMap, typeof(string), "PropertyMapping", hashPrefix, (IVariableSource)null) as string;
                // sharedVariable.PropertyMappingOwner =
                //     BinaryDeserialization.LoadField(fieldSerializationData, fieldIndexMap, typeof(GameObject), "PropertyMappingOwner", hashPrefix, (IVariableSource)null) as GameObject;
                sharedVariable.InitializePropertyMapping(variableSource as BehaviorSource);
            }
            if (!flag)
            {
                sharedVariable.IsShared = true;
            }

            if (boolean3 && BehaviorManager.IsPlaying)
            {
                variableSource.SetVariable(name, sharedVariable);
            }

            BinaryDeserialization.LoadFields(
                fieldSerializationData, fieldIndexMap, (object)sharedVariable, hashPrefix, variableSource);
        }
        return sharedVariable;
    }
#endif

    private class ObjectFieldMap
    {
        public object obj;
        public FieldInfo fieldInfo;

        public ObjectFieldMap(object o, FieldInfo f)
        {
            this.obj = o;
            this.fieldInfo = f;
        }
    }

    private class ObjectFieldMapComparer : IEqualityComparer<BinaryDeserialization.ObjectFieldMap>
    {
        public bool Equals(
            BinaryDeserialization.ObjectFieldMap a,
            BinaryDeserialization.ObjectFieldMap b
        )
        {
            return !object.ReferenceEquals((object)a, (object)null)
                && !object.ReferenceEquals((object)b, (object)null)
                && a.obj.Equals(b.obj)
                && a.fieldInfo.Equals((object)b.fieldInfo);
        }

        public int GetHashCode(BinaryDeserialization.ObjectFieldMap a)
        {
            return a != null ? a.obj.ToString().GetHashCode() + a.fieldInfo.ToString().GetHashCode() : 0;
        }
    }
}
