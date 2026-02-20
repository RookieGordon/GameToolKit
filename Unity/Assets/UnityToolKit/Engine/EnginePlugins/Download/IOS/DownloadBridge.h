/*
 * datetime     : 2026/2/20
 * description  : iOS 后台下载桥接 - 头文件
 *
 * 最小权限: 仅使用 UIBackgroundModes: fetch
 * 通过 beginBackgroundTaskWithExpirationHandler 延长后台执行时间
 * 通过 UNUserNotificationCenter 显示本地通知
 */

#import <Foundation/Foundation.h>

@interface DownloadBridge : NSObject

/// 请求通知权限 (仅 alert + sound, 不申请 badge)
+ (void)requestNotificationPermission;

/// 开始后台任务, 延长 Unity 进入后台后的执行时间
+ (void)beginBackgroundTask;

/// 结束后台任务
+ (void)endBackgroundTask;

/// 显示/更新本地下载进度通知
/// @param title   通知标题
/// @param content 通知正文
/// @param progress 进度 (0.0~1.0)
+ (void)showNotificationWithTitle:(NSString *)title
                          content:(NSString *)content
                         progress:(float)progress;

/// 移除下载进度通知
+ (void)hideNotification;

/// 启用前台通知显示
/// 默认 iOS 前台时不展示本地通知, 调用此方法后下载通知将在前台以 Banner 形式显示
/// 安全地与已有 UNUserNotificationCenterDelegate 链式共存
+ (void)enableForegroundNotification;

@end
