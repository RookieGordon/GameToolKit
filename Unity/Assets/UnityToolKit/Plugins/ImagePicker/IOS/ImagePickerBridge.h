/*
 * datetime     : 2026/2/24
 * description  : iOS 图片选择器桥接 - 头文件
 *
 * 使用 UIImagePickerController (拍照) 和 PHPickerViewController (图库, iOS 14+)
 * 裁剪使用 TOCropViewController (开源库), 通过反射调用避免硬依赖
 * 支持图片约束校验、裁剪、压缩
 */

#import <Foundation/Foundation.h>

@interface ImagePickerBridge : NSObject

/// 初始化, 设置 Unity 回调 GameObject 名称
/// @param gameObjectName Unity 中接收回调的 GameObject 名称
+ (void)initWithGameObjectName:(NSString *)gameObjectName;

/// 开始选图/拍照流程
/// @param configJson JSON 配置字符串
+ (void)pickImageWithConfig:(NSString *)configJson;

/// 检查相机是否可用
+ (BOOL)isCameraAvailable;

@end
