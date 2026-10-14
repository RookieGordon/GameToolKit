/*
 * 测试可见性：ToolKit.Tests 需要访问 ResourceSystem 的内部流程类型，
 * 用于验证"等待者是否全部结算""额度是否归还"这类流程内部不变式，
 * 以及在排查挂起时读取流程内部的诊断轨迹。
 * 与 ToolKit.Common 的 AssemblyInfo.cs 保持同一约定。
 */

using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Tests")]
