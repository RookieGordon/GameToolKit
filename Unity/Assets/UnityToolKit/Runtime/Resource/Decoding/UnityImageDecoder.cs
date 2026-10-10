/*
 * author       : Gordon
 * datetime     : 2026/10/9
 * description  : Unity 图片解码器 (P5, §8.2)。从文件字节创建 Texture2D 或 Sprite；
 *                纹理创建在主线程执行 (Unity API 约束)；结果完全读入，不依赖源文件
 *                释放即 Destroy 底层纹理。
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
    public sealed class UnityImageDecoder : IResourceDecoder, IRepresentationDecoder
    {
        public const string DecoderId = "unity-image";

        private readonly IExecutionContext _context;

        public UnityImageDecoder(IExecutionContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        public string Id => DecoderId;

        public bool CanDecode(Type resultType)
        {
            return resultType == typeof(Texture2D) || resultType == typeof(Sprite);
        }

        public async Task<LoadedAsset> DecodeAsync(string path, Type resultType, object? parameters, CancellationToken ct)
        {
            byte[] bytes;
            try
            {
                bytes = await ResourceFileIo.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // 主动取消不是文件 I/O 故障 (R22)
            }
            catch (Exception ex)
            {
                throw new ResourceLoadException(_ReadError(path, ex));
            }

            try
            {
                return await _context.RunAsync(() =>
                {
                    ct.ThrowIfCancellationRequested();
                    if (!CanDecode(resultType))
                    {
                        throw new ResourceLoadException(new LoadError(
                            DiagnosticCodes.AssetUnsupportedRepresentation, LoadStage.Decode, CleanupStatus.Complete, null,
                            new Dictionary<string, object> { { "resultType", resultType.Name } }));
                    }
                    var texture = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: false);
                    try
                    {
                        if (!texture.LoadImage(bytes, markNonReadable: true))
                        {
                            throw new ResourceLoadException(new LoadError(
                                DiagnosticCodes.AssetDecodeFailed, LoadStage.Decode, CleanupStatus.Complete, null,
                                new Dictionary<string, object> { { "path", path }, { "reason", "load-image-failed" } }));
                        }
                        var estimatedBytes = texture.width * texture.height * 4L;
                        if (resultType == typeof(Texture2D))
                        {
                            return new LoadedAsset(texture, estimatedBytes,
                                isAlive: () => texture != null,
                                releaseAsync: () => _DestroyOnMain(texture));
                        }
                        var sprite = Sprite.Create(texture,
                            new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f), 100f);
                        return new LoadedAsset(sprite, estimatedBytes,
                            isAlive: () => sprite != null,
                            releaseAsync: () => _context.RunAsync(() =>
                            {
                                if (sprite != null) Object.Destroy(sprite);
                                if (texture != null) Object.Destroy(texture);
                            }));
                    }
                    catch
                    {
                        Object.Destroy(texture);
                        throw;
                    }
                }).ConfigureAwait(false);
            }
            catch (ResourceLoadException)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new ResourceLoadException(new LoadError(
                    DiagnosticCodes.AssetDecodeFailed, LoadStage.Decode, CleanupStatus.Unknown, ex,
                    new Dictionary<string, object> { { "path", path } }));
            }
        }

        private Task _DestroyOnMain(Texture2D texture)
        {
            return _context.RunAsync(() =>
            {
                if (texture != null)
                {
                    Object.Destroy(texture);
                }
            });
        }

        private static LoadError _ReadError(string path, Exception ex)
        {
            var code = ex is FileNotFoundException || ex is DirectoryNotFoundException
                ? DiagnosticCodes.FileNotFound
                : FileSystemErrorClassifier.IsDiskFull(ex)
                    ? DiagnosticCodes.CacheDiskFull
                    : FileSystemErrorClassifier.IsAccessDenied(ex)
                        ? DiagnosticCodes.FileAccessDenied
                        : DiagnosticCodes.FileIoFailed;
            return new LoadError(code, LoadStage.ReadFile, CleanupStatus.Complete, ex,
                new Dictionary<string, object> { { "path", path } });
        }
    }
}
