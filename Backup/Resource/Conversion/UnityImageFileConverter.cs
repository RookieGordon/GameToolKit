/*
 * author       : Gordon
 * datetime     : 2026/10/9
 * description  : 文件转 Texture2D/Sprite。每次转换创建独立的 Unity 对象，
 *                在主线程创建和销毁；失败残留随异常交给核心隔离，不影响下一次转换。
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ToolKit.Tools.Common;
using UnityEngine;
using Object = UnityEngine.Object;

namespace UnityToolKit.Runtime.Resource
{
    public sealed class UnityImageFileConverter : IFileResourceConverter
    {
        private readonly IExecutionContext _context;

        public UnityImageFileConverter(IExecutionContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        public async Task<LoadedAsset> ConvertAsync(string path, Type resultType, string? conversionKey, CancellationToken ct)
        {
            if (resultType != typeof(Texture2D) && resultType != typeof(Sprite))
            {
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.AssetUnsupportedRepresentation, LoadStage.Convert, CleanupStatus.Complete, null,
                    new Dictionary<string, object> { { "resultType", resultType.Name } }));
            }
            byte[] bytes;
            try
            {
                ct.ThrowIfCancellationRequested();
                bytes = await ResourceFileIo.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                throw new ResourceLoadException(FileSystemErrorClassifier.ToLoadError(path, ex, LoadStage.ReadFile));
            }

            return await _context.RunAsync(() =>
            {
                ct.ThrowIfCancellationRequested();
                Texture2D? texture = null;
                Sprite? sprite = null;
                try
                {
                    texture = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: false);
                    if (!texture.LoadImage(bytes, markNonReadable: true))
                    {
                        throw new InvalidDataException("图片内容无法转换为纹理");
                    }
                    if (resultType == typeof(Texture2D))
                    {
                        return new LoadedAsset(texture,
                            isAlive: () => texture != null,
                            releaseAsync: () => _context.RunAsync(() => _DestroyOwnedObjects(texture)));
                    }
                    sprite = Sprite.Create(texture,
                        new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f), 100f);
                    if (sprite == null)
                    {
                        throw new InvalidOperationException("Sprite.Create 未返回有效对象");
                    }
                    return new LoadedAsset(sprite,
                        isAlive: () => sprite != null && texture != null,
                        releaseAsync: () => _context.RunAsync(() => _DestroyOwnedObjects(sprite, texture)));
                }
                catch (Exception ex)
                {
                    var error = new LoadError(
                        DiagnosticCodes.AssetConversionFailed, LoadStage.Convert, CleanupStatus.Complete, ex,
                        new Dictionary<string, object> { { "path", path } });
                    try
                    {
                        _DestroyOwnedObjects(sprite, texture);
                    }
                    catch (ResourceLoadException cleanup)
                    {
                        throw new ResourceLoadException(error.WithRelated(cleanup.Error), cleanup.CleanupRemainders);
                    }
                    throw new ResourceLoadException(error);
                }
            }).ConfigureAwait(false);
        }

        /// <summary> 每个对象分别尝试一次；一个对象失败不阻止其余对象释放。不自动重试。 </summary>
        private static void _DestroyOwnedObjects(params Object?[] objects)
        {
            List<object>? remainders = null;
            List<Exception>? errors = null;
            foreach (var value in objects)
            {
                try
                {
                    if (value != null) Object.Destroy(value);
                    // Destroy 正常返回表示销毁请求已接受，不把 Unity 的帧末延迟误判为失败。
                }
                catch (Exception ex)
                {
                    (remainders ??= new List<object>()).Add(value!);
                    (errors ??= new List<Exception>()).Add(ex);
                }
            }
            if (remainders != null)
            {
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.LifecycleReleaseFailed, LoadStage.ReleaseAsset, CleanupStatus.Incomplete,
                    new AggregateException(errors!)), remainders);
            }
        }

    }
}
