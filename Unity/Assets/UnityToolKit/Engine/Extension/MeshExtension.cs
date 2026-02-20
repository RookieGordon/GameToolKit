/*
 * 功能描述：Mesh 扩展方法
 */

using UnityEngine;

namespace UnityToolKit.Engine.Extension
{
    public static class MeshExtension
    {
        /// <summary>
        /// 深拷贝 Mesh
        /// </summary>
        public static Mesh Copy(this Mesh source)
        {
            var mesh = new Mesh
            {
                name = source.name,
                vertices = source.vertices,
                normals = source.normals,
                tangents = source.tangents,
                uv = source.uv,
                uv2 = source.uv2,
                uv3 = source.uv3,
                uv4 = source.uv4,
                colors = source.colors,
                colors32 = source.colors32,
                triangles = source.triangles,
                subMeshCount = source.subMeshCount,
                indexFormat = source.indexFormat,
                boneWeights = source.boneWeights,
                bindposes = source.bindposes,
                bounds = source.bounds,
            };

            for (int i = 0; i < source.subMeshCount; i++)
            {
                mesh.SetSubMesh(i, source.GetSubMesh(i));
            }

            return mesh;
        }
    }
}
