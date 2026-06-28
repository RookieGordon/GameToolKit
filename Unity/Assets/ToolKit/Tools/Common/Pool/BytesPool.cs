/*
 * author       : Gordon
 * datetime     : 2026/6/26
 * description  : 字节缓冲区池 (引擎无关), 对 System.Buffers.ArrayPool<byte> 的轻封装。
 *                用于复用文件/网络读取时的临时读缓冲, 降低大文件读取的堆分配与 GC。
 *                约定: Rent 得到的数组长度 >= 申请值, 用完务必 Return。
 */

using System.Buffers;

namespace ToolKit.Tools.Common
{
    public static class BytesPool
    {
        /// <summary> 借出一个长度至少为 minSize 的字节数组 </summary>
        public static byte[] Rent(int minSize)
        {
            return ArrayPool<byte>.Shared.Rent(minSize);
        }

        /// <summary> 归还字节数组。clearArray=true 时清零 (含敏感数据时使用) </summary>
        public static void Return(byte[] buffer, bool clearArray = false)
        {
            if (buffer != null)
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray);
            }
        }
    }
}
