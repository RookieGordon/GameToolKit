/*
 * datetime     : 2026/2/24
 * description  : iOS 图片选择器桥接 - 实现
 *
 * 选图流程:
 *   1. 解析 JSON 配置
 *   2. 拍照使用 UIImagePickerController, 图库使用 PHPickerViewController (iOS 14+)
 *      或 UIImagePickerController (低版本)
 *   3. 获取图片后进行约束校验
 *   4. 需要裁剪时使用 TOCropViewController (如已集成) 或跳过
 *   5. 以高质量保存到临时文件, 压缩在 Unity C# 侧统一处理
 *   6. 通过 UnitySendMessage 回调结果
 *
 * 权限:
 *   - 拍照: 需要 NSCameraUsageDescription (Info.plist)
 *     系统会在首次访问时自动弹出权限对话框
 *     如果用户拒绝, UIImagePickerController 会调用 didCancel 回调
 *   - 图库: iOS 14+ 使用 PHPickerViewController 不需要权限
 *           低版本需要 NSPhotoLibraryUsageDescription
 *
 * 裁剪库:
 *   - 优先使用 TOCropViewController (需通过 CocoaPods/SPM 集成)
 *   - 如未集成, 跳过裁剪步骤
 */

#import "ImagePickerBridge.h"
#import <UIKit/UIKit.h>
#import <Photos/Photos.h>
#import <PhotosUI/PhotosUI.h>

// ---- Unity 回调 ----

static NSString *_unityGameObjectName = nil;

extern void UnitySendMessage(const char *obj, const char *method, const char *msg);

static void SendToUnity(NSString *method, NSString *message) {
    if (_unityGameObjectName == nil) return;
    UnitySendMessage(
        [_unityGameObjectName UTF8String],
        [method UTF8String],
        [message UTF8String]
    );
}

// ---- 配置解析 ----

static NSDictionary *_currentConfig = nil;

// ---- Delegate 对象 (持有引用防止释放) ----

@interface ToolKitImagePickerDelegate : NSObject
    <UIImagePickerControllerDelegate, UINavigationControllerDelegate>
@end

#if __IPHONE_OS_VERSION_MAX_ALLOWED >= 140000
@interface ToolKitPHPickerDelegate : NSObject <PHPickerViewControllerDelegate>
@end
#endif

static ToolKitImagePickerDelegate *_imagePickerDelegate = nil;

#if __IPHONE_OS_VERSION_MAX_ALLOWED >= 140000
static ToolKitPHPickerDelegate *_phPickerDelegate = nil;
#endif

// ---- 图片处理工具方法 ----

/**
 * 修正 UIImage 方向
 * 相机拍摄的照片 UIImage.imageOrientation 可能不是 UIImageOrientationUp,
 * JPEG 编码会将方向信息写入 EXIF, 但 Unity 的 Texture2D.LoadImage 不会解析 EXIF.
 * 此方法将像素数据重新绘制为正确方向, 保证编码后无需 EXIF 即可正确显示.
 */
static UIImage * NormalizeOrientation(UIImage *image) {
    if (image.imageOrientation == UIImageOrientationUp) {
        return image;
    }

    UIGraphicsBeginImageContextWithOptions(image.size, NO, image.scale);
    [image drawInRect:CGRectMake(0, 0, image.size.width, image.size.height)];
    UIImage *normalized = UIGraphicsGetImageFromCurrentImageContext();
    UIGraphicsEndImageContext();
    return normalized ?: image;
}

static NSString * SaveImageToTemp(NSData *data) {
    NSString *dir = [NSTemporaryDirectory() stringByAppendingPathComponent:@"ImagePicker"];
    [[NSFileManager defaultManager] createDirectoryAtPath:dir
                              withIntermediateDirectories:YES attributes:nil error:nil];
    NSString *filename = [NSString stringWithFormat:@"result_%lld.jpg",
                          (long long)([[NSDate date] timeIntervalSince1970] * 1000)];
    NSString *path = [dir stringByAppendingPathComponent:filename];
    [data writeToFile:path atomically:YES];
    return path;
}

static NSString * ValidateConstraints(UIImage *image, long fileSize, NSDictionary *config) {
    int w = (int)image.size.width;
    int h = (int)image.size.height;

    long maxFileSize = [config[@"maxFileSize"] longValue];
    int minWidth = [config[@"minWidth"] intValue];
    int minHeight = [config[@"minHeight"] intValue];
    int maxWidth = [config[@"maxWidth"] intValue];
    int maxHeight = [config[@"maxHeight"] intValue];

    if (maxFileSize > 0 && fileSize > maxFileSize)
        return [NSString stringWithFormat:@"文件大小 (%ld bytes) 超过限制 (%ld bytes)",
                fileSize, maxFileSize];

    if (minWidth > 0 && w < minWidth)
        return [NSString stringWithFormat:@"图片宽度 (%dpx) 小于最小限制 (%dpx)", w, minWidth];

    if (minHeight > 0 && h < minHeight)
        return [NSString stringWithFormat:@"图片高度 (%dpx) 小于最小限制 (%dpx)", h, minHeight];

    if (maxWidth > 0 && w > maxWidth)
        return [NSString stringWithFormat:@"图片宽度 (%dpx) 超过最大限制 (%dpx)", w, maxWidth];

    if (maxHeight > 0 && h > maxHeight)
        return [NSString stringWithFormat:@"图片高度 (%dpx) 超过最大限制 (%dpx)", h, maxHeight];

    return nil;
}

static void ProcessAndReturnImage(UIImage *image, long originalFileSize);
static void StartCrop(UIImage *image);
static void SaveAndReturn(UIImage *image);

// ---- 处理流程 ----

static void ProcessAndReturnImage(UIImage *image, long originalFileSize) {
    if (image == nil) {
        SendToUnity(@"OnImagePickerFailed", @"31");
        return;
    }

    // 约束校验
    NSString *error = ValidateConstraints(image, originalFileSize, _currentConfig);
    if (error != nil) {
        SendToUnity(@"OnImagePickerFailed",
                    [NSString stringWithFormat:@"40|%@", error]);
        return;
    }

    // 裁剪
    BOOL enableCrop = [_currentConfig[@"enableCrop"] boolValue];
    if (enableCrop) {
        StartCrop(image);
        return;
    }

    // 直接保存返回 (压缩在 Unity C# 侧统一处理)
    SaveAndReturn(image);
}

static void StartCrop(UIImage *image) {
    // 尝试使用 TOCropViewController (通过反射, 避免硬依赖)
    Class tocropClass = NSClassFromString(@"TOCropViewController");

    if (tocropClass != nil) {
        @try {
            int cropShape = [_currentConfig[@"cropShape"] intValue];

            // 创建 TOCropViewController
            id cropVC = [[tocropClass alloc] performSelector:@selector(initWithImage:)
                                                  withObject:image];

            // 设置裁剪形状
            if (cropShape == 1) { // Circle
                // TOCropViewCroppingStyle: circular = 1
                // 使用 initWithCroppingStyle:image: 初始化
                SEL initSel = NSSelectorFromString(@"initWithCroppingStyle:image:");
                if ([tocropClass instancesRespondToSelector:initSel]) {
                    NSMethodSignature *sig = [tocropClass instanceMethodSignatureForSelector:initSel];
                    NSInvocation *inv = [NSInvocation invocationWithMethodSignature:sig];
                    [inv setSelector:initSel];
                    [inv setTarget:[[tocropClass alloc] init]];
                    NSInteger style = 1; // circular
                    [inv setArgument:&style atIndex:2];
                    [inv setArgument:&image atIndex:3];
                    [inv invoke];
                    __unsafe_unretained id result;
                    [inv getReturnValue:&result];
                    cropVC = result;
                }
            }

            // 设置宽高比
            float aspectX = [_currentConfig[@"aspectRatioX"] floatValue];
            float aspectY = [_currentConfig[@"aspectRatioY"] floatValue];
            if (aspectX > 0 && aspectY > 0) {
                SEL aspectSel = NSSelectorFromString(@"setCustomAspectRatio:");
                if ([cropVC respondsToSelector:aspectSel]) {
                    NSMethodSignature *sig = [[cropVC class] instanceMethodSignatureForSelector:aspectSel];
                    NSInvocation *inv = [NSInvocation invocationWithMethodSignature:sig];
                    [inv setSelector:aspectSel];
                    [inv setTarget:cropVC];
                    CGSize ratio = CGSizeMake(aspectX, aspectY);
                    [inv setArgument:&ratio atIndex:2];
                    [inv invoke];
                }

                // 锁定宽高比
                SEL lockSel = NSSelectorFromString(@"setAspectRatioLockEnabled:");
                if ([cropVC respondsToSelector:lockSel]) {
                    NSMethodSignature *sig = [[cropVC class] instanceMethodSignatureForSelector:lockSel];
                    NSInvocation *inv = [NSInvocation invocationWithMethodSignature:sig];
                    [inv setSelector:lockSel];
                    [inv setTarget:cropVC];
                    BOOL locked = YES;
                    [inv setArgument:&locked atIndex:2];
                    [inv invoke];
                }
            }

            // 设置 delegate (使用通知或者 block 方式)
            // TOCropViewController 使用 delegate 模式, 我们通过 NSNotification 桥接
            SEL delegateSel = NSSelectorFromString(@"setDelegate:");
            if ([cropVC respondsToSelector:delegateSel]) {
                // 创建代理对象处理裁剪完成回调
                // 简化: 使用 block-based 完成回调
                SEL onDoneSel = NSSelectorFromString(@"setOnDidCropToRect:");
                if ([cropVC respondsToSelector:onDoneSel]) {
                    void (^doneBlock)(UIImage *) = ^(UIImage *croppedImage) {
                        dispatch_async(dispatch_get_main_queue(), ^{
                            SaveAndReturn(croppedImage);
                        });

                        UIViewController *rootVC = [UIApplication sharedApplication]
                            .keyWindow.rootViewController;
                        [rootVC dismissViewControllerAnimated:YES completion:nil];
                    };
                    [cropVC performSelector:onDoneSel withObject:[doneBlock copy]];
                }
            }

            // 显示裁剪界面
            UIViewController *rootVC = [UIApplication sharedApplication]
                .keyWindow.rootViewController;
            // 获取最顶层的 presented VC
            while (rootVC.presentedViewController) {
                rootVC = rootVC.presentedViewController;
            }
            [rootVC presentViewController:cropVC animated:YES completion:nil];
            return;

        } @catch (NSException *exception) {
            NSLog(@"[ToolKit ImagePicker] TOCropViewController 调用失败: %@", exception);
        }
    }

    // TOCropViewController 不可用, 回退: 直接保存返回 (不裁剪)
    NSLog(@"[ToolKit ImagePicker] TOCropViewController 不可用, 跳过裁剪");
    SaveAndReturn(image);
}

static void SaveAndReturn(UIImage *image) {
    // 修正方向: 将像素数据旋转到正确方向, 避免 Unity 渲染时图片旋转
    UIImage *normalized = NormalizeOrientation(image);
    int width = (int)normalized.size.width;
    int height = (int)normalized.size.height;

    // 以高质量 JPEG 保存到临时文件, 压缩由 Unity C# 侧统一处理
    NSData *jpgData = UIImageJPEGRepresentation(normalized, 0.95);
    NSString *path = SaveImageToTemp(jpgData);
    NSString *result = [NSString stringWithFormat:@"%@|%d|%d|%ld",
                        path, width, height, (long)jpgData.length];
    SendToUnity(@"OnImagePickerSuccess", result);
}

// ======== UIImagePickerController Delegate ========

@implementation ToolKitImagePickerDelegate

- (void)imagePickerController:(UIImagePickerController *)picker
didFinishPickingMediaWithInfo:(NSDictionary<UIImagePickerControllerInfoKey,id> *)info {

    [picker dismissViewControllerAnimated:YES completion:^{
        UIImage *image = info[UIImagePickerControllerOriginalImage];
        if (image == nil) {
            image = info[UIImagePickerControllerEditedImage];
        }

        long fileSize = 0;
        NSURL *imageURL = info[UIImagePickerControllerImageURL];
        if (imageURL != nil) {
            NSNumber *fileSizeValue = nil;
            [imageURL getResourceValue:&fileSizeValue forKey:NSURLFileSizeKey error:nil];
            fileSize = [fileSizeValue longValue];
        }
        if (fileSize == 0) {
            // 估算大小
            NSData *data = UIImageJPEGRepresentation(image, 1.0);
            fileSize = data.length;
        }

        ProcessAndReturnImage(image, fileSize);
    }];
}

- (void)imagePickerControllerDidCancel:(UIImagePickerController *)picker {
    [picker dismissViewControllerAnimated:YES completion:^{
        SendToUnity(@"OnImagePickerCancelled", @"");
    }];
}

@end

// ======== PHPickerViewController Delegate (iOS 14+) ========

#if __IPHONE_OS_VERSION_MAX_ALLOWED >= 140000

@implementation ToolKitPHPickerDelegate

- (void)picker:(PHPickerViewController *)picker
didFinishPicking:(NSArray<PHPickerResult *> *)results API_AVAILABLE(ios(14)) {

    [picker dismissViewControllerAnimated:YES completion:^{
        if (results.count == 0) {
            SendToUnity(@"OnImagePickerCancelled", @"");
            return;
        }

        PHPickerResult *result = results.firstObject;
        NSItemProvider *provider = result.itemProvider;

        if ([provider canLoadObjectOfClass:[UIImage class]]) {
            [provider loadObjectOfClass:[UIImage class]
                      completionHandler:^(id<NSItemProviding> object, NSError *error) {
                dispatch_async(dispatch_get_main_queue(), ^{
                    if (error != nil) {
                        SendToUnity(@"OnImagePickerFailed",
                                    [NSString stringWithFormat:@"33|%@",
                                     error.localizedDescription]);
                        return;
                    }

                    UIImage *image = (UIImage *)object;
                    NSData *data = UIImageJPEGRepresentation(image, 1.0);
                    long fileSize = data ? data.length : 0;
                    ProcessAndReturnImage(image, fileSize);
                });
            }];
        } else {
            SendToUnity(@"OnImagePickerFailed", @"32");
        }
    }];
}

@end

#endif

// ======== ImagePickerBridge 实现 ========

@implementation ImagePickerBridge

+ (void)initWithGameObjectName:(NSString *)gameObjectName {
    _unityGameObjectName = [gameObjectName copy];
}

+ (BOOL)isCameraAvailable {
    return [UIImagePickerController isSourceTypeAvailable:UIImagePickerControllerSourceTypeCamera];
}

+ (void)pickImageWithConfig:(NSString *)configJson {
    NSError *jsonError = nil;
    NSDictionary *config = [NSJSONSerialization JSONObjectWithData:
                            [configJson dataUsingEncoding:NSUTF8StringEncoding]
                                                          options:0
                                                            error:&jsonError];
    if (jsonError != nil || config == nil) {
        SendToUnity(@"OnImagePickerFailed", @"91");
        return;
    }

    _currentConfig = config;

    int source = [config[@"source"] intValue];

    dispatch_async(dispatch_get_main_queue(), ^{
        if (source == 1) {
            [self openCamera];
        } else {
            [self openGallery];
        }
    });
}

+ (void)openCamera {
    if (![UIImagePickerController isSourceTypeAvailable:
          UIImagePickerControllerSourceTypeCamera]) {
        SendToUnity(@"OnImagePickerFailed", @"20");
        return;
    }

    if (_imagePickerDelegate == nil) {
        _imagePickerDelegate = [[ToolKitImagePickerDelegate alloc] init];
    }

    UIImagePickerController *picker = [[UIImagePickerController alloc] init];
    picker.sourceType = UIImagePickerControllerSourceTypeCamera;
    picker.delegate = _imagePickerDelegate;
    picker.allowsEditing = NO;

    UIViewController *rootVC = [UIApplication sharedApplication].keyWindow.rootViewController;
    while (rootVC.presentedViewController) {
        rootVC = rootVC.presentedViewController;
    }
    [rootVC presentViewController:picker animated:YES completion:nil];
}

+ (void)openGallery {
    // iOS 14+ 使用 PHPickerViewController (无需权限申请)
    if (@available(iOS 14.0, *)) {
#if __IPHONE_OS_VERSION_MAX_ALLOWED >= 140000
        if (_phPickerDelegate == nil) {
            _phPickerDelegate = [[ToolKitPHPickerDelegate alloc] init];
        }

        PHPickerConfiguration *pickerConfig =
            [[PHPickerConfiguration alloc] initWithPhotoLibrary:
             [PHPhotoLibrary sharedPhotoLibrary]];
        pickerConfig.selectionLimit = 1;
        pickerConfig.filter = [PHPickerFilter imagesFilter];

        PHPickerViewController *picker =
            [[PHPickerViewController alloc] initWithConfiguration:pickerConfig];
        picker.delegate = _phPickerDelegate;

        UIViewController *rootVC = [UIApplication sharedApplication].keyWindow.rootViewController;
        while (rootVC.presentedViewController) {
            rootVC = rootVC.presentedViewController;
        }
        [rootVC presentViewController:picker animated:YES completion:nil];
        return;
#endif
    }

    // 低版本回退到 UIImagePickerController
    if (_imagePickerDelegate == nil) {
        _imagePickerDelegate = [[ToolKitImagePickerDelegate alloc] init];
    }

    UIImagePickerController *picker = [[UIImagePickerController alloc] init];
    picker.sourceType = UIImagePickerControllerSourceTypePhotoLibrary;
    picker.delegate = _imagePickerDelegate;
    picker.allowsEditing = NO;

    UIViewController *rootVC = [UIApplication sharedApplication].keyWindow.rootViewController;
    while (rootVC.presentedViewController) {
        rootVC = rootVC.presentedViewController;
    }
    [rootVC presentViewController:picker animated:YES completion:nil];
}

@end

// ======== C 函数导出 (供 Unity P/Invoke 调用) ========

#ifdef __cplusplus
extern "C" {
#endif

void ToolKit_ImagePicker_Init(const char *gameObjectName) {
    NSString *name = gameObjectName
        ? [NSString stringWithUTF8String:gameObjectName] : nil;
    [ImagePickerBridge initWithGameObjectName:name];
}

void ToolKit_ImagePicker_PickImage(const char *configJson) {
    NSString *json = configJson
        ? [NSString stringWithUTF8String:configJson] : nil;
    [ImagePickerBridge pickImageWithConfig:json];
}

bool ToolKit_ImagePicker_IsCameraAvailable() {
    return [ImagePickerBridge isCameraAvailable];
}

#ifdef __cplusplus
}
#endif
