/*
 * datetime     : 2026/2/20
 * description  : iOS 后台下载桥接 - 实现
 *
 * 后台策略:
 *   使用 beginBackgroundTaskWithExpirationHandler 延长后台执行时间 (约 30 秒~3 分钟)
 *   到期时自动尝试重新申请, 尽量延长后台时间
 *
 * 通知策略:
 *   使用 UNUserNotificationCenter 本地通知显示进度
 *   通知 ID 固定, 更新时替换已有通知
 *   支持前台通知显示 (通过 UNUserNotificationCenterDelegate 链式代理)
 *
 * 最小权限:
 *   - UNAuthorizationOptionAlert | UNAuthorizationOptionSound (不申请 badge)
 *   - 无需额外 entitlements 或 capabilities
 */

#import "DownloadBridge.h"
#import <UIKit/UIKit.h>
#import <UserNotifications/UserNotifications.h>

static UIBackgroundTaskIdentifier _bgTaskId = UIBackgroundTaskInvalid;
static BOOL _shouldRenewBackgroundTask = NO;
static NSString * const kNotificationIdentifier = @"toolkit_download_progress";

#pragma mark - 前台通知代理 (链式)

/// 前台通知代理, 仅拦截下载相关通知, 其余转发给原始代理
@interface ToolKitDownloadNotificationDelegate : NSObject <UNUserNotificationCenterDelegate>
@property (nonatomic, weak) id<UNUserNotificationCenterDelegate> originalDelegate;
@end

@implementation ToolKitDownloadNotificationDelegate

/// 前台收到通知时: 下载通知显示 Banner, 其余转发原始代理
- (void)userNotificationCenter:(UNUserNotificationCenter *)center
       willPresentNotification:(UNNotification *)notification
         withCompletionHandler:(void (^)(UNNotificationPresentationOptions))completionHandler {
    if ([notification.request.identifier isEqualToString:kNotificationIdentifier]) {
        if (@available(iOS 14.0, *)) {
            completionHandler(UNNotificationPresentationOptionBanner);
        } else {
            completionHandler(UNNotificationPresentationOptionAlert);
        }
    } else if (_originalDelegate &&
               [_originalDelegate respondsToSelector:
                @selector(userNotificationCenter:willPresentNotification:withCompletionHandler:)]) {
        [_originalDelegate userNotificationCenter:center
                      willPresentNotification:notification
                        withCompletionHandler:completionHandler];
    } else {
        completionHandler(UNNotificationPresentationOptionNone);
    }
}

/// 通知点击响应: 转发给原始代理
- (void)userNotificationCenter:(UNUserNotificationCenter *)center
didReceiveNotificationResponse:(UNNotificationResponse *)response
         withCompletionHandler:(void (^)(void))completionHandler {
    if (_originalDelegate &&
        [_originalDelegate respondsToSelector:
         @selector(userNotificationCenter:didReceiveNotificationResponse:withCompletionHandler:)]) {
        [_originalDelegate userNotificationCenter:center
                 didReceiveNotificationResponse:response
                          withCompletionHandler:completionHandler];
    } else {
        completionHandler();
    }
}

@end

static ToolKitDownloadNotificationDelegate *_foregroundDelegate = nil;

#pragma mark - DownloadBridge 实现

@implementation DownloadBridge

#pragma mark - 通知权限

+ (void)requestNotificationPermission {
    UNUserNotificationCenter *center = [UNUserNotificationCenter currentNotificationCenter];
    // 最小权限: 仅申请 alert + sound
    [center requestAuthorizationWithOptions:(UNAuthorizationOptionAlert | UNAuthorizationOptionSound)
                          completionHandler:^(BOOL granted, NSError * _Nullable error) {
        if (error) {
            NSLog(@"[ToolKit Download] 通知权限申请失败: %@", error.localizedDescription);
        }
    }];
}

#pragma mark - 后台任务

+ (void)beginBackgroundTask {
    _shouldRenewBackgroundTask = YES;
    [self _startBackgroundTask];
}

+ (void)endBackgroundTask {
    _shouldRenewBackgroundTask = NO;

    if (_bgTaskId == UIBackgroundTaskInvalid) return;

    [[UIApplication sharedApplication] endBackgroundTask:_bgTaskId];
    _bgTaskId = UIBackgroundTaskInvalid;
}

/// 内部方法: 申请/重新申请后台任务
+ (void)_startBackgroundTask {
    // 先结束旧的后台任务 (如果存在)
    if (_bgTaskId != UIBackgroundTaskInvalid) {
        [[UIApplication sharedApplication] endBackgroundTask:_bgTaskId];
        _bgTaskId = UIBackgroundTaskInvalid;
    }

    _bgTaskId = [[UIApplication sharedApplication]
        beginBackgroundTaskWithExpirationHandler:^{
            // 即将到期, 尝试重新申请以延长后台时间
            UIBackgroundTaskIdentifier expiredTaskId = _bgTaskId;
            _bgTaskId = UIBackgroundTaskInvalid;

            if (_shouldRenewBackgroundTask) {
                [self _startBackgroundTask];
            }

            // 结束已过期的任务
            if (expiredTaskId != UIBackgroundTaskInvalid) {
                [[UIApplication sharedApplication] endBackgroundTask:expiredTaskId];
            }
        }];

    // 申请失败时清理状态
    if (_bgTaskId == UIBackgroundTaskInvalid) {
        _shouldRenewBackgroundTask = NO;
        NSLog(@"[ToolKit Download] 后台任务申请失败, 系统拒绝了请求");
    }
}

#pragma mark - 本地通知

+ (void)showNotificationWithTitle:(NSString *)title
                          content:(NSString *)content
                         progress:(float)progress {
    UNMutableNotificationContent *nContent = [[UNMutableNotificationContent alloc] init];
    nContent.title = title ? title : @"下载中";

    // 将进度信息拼接到通知正文
    int percent = (int)(progress * 100);
    if (percent < 0) percent = 0;
    if (percent > 100) percent = 100;

    if (content) {
        nContent.body = [NSString stringWithFormat:@"%@ (%d%%)", content, percent];
    } else {
        nContent.body = [NSString stringWithFormat:@"下载进度: %d%%", percent];
    }

    nContent.sound = nil; // 进度更新不发声

    // 立即触发 (1 秒后), 替换已有通知
    UNTimeIntervalNotificationTrigger *trigger =
        [UNTimeIntervalNotificationTrigger triggerWithTimeInterval:1 repeats:NO];

    UNNotificationRequest *request =
        [UNNotificationRequest requestWithIdentifier:kNotificationIdentifier
                                             content:nContent
                                             trigger:trigger];

    [[UNUserNotificationCenter currentNotificationCenter]
        addNotificationRequest:request
         withCompletionHandler:^(NSError * _Nullable error) {
            if (error) {
                NSLog(@"[ToolKit Download] 通知发送失败: %@", error.localizedDescription);
            }
        }];
}

+ (void)hideNotification {
    [[UNUserNotificationCenter currentNotificationCenter]
        removeDeliveredNotificationsWithIdentifiers:@[kNotificationIdentifier]];
    [[UNUserNotificationCenter currentNotificationCenter]
        removePendingNotificationRequestsWithIdentifiers:@[kNotificationIdentifier]];
}

#pragma mark - 前台通知

+ (void)enableForegroundNotification {
    if (_foregroundDelegate == nil) {
        _foregroundDelegate = [[ToolKitDownloadNotificationDelegate alloc] init];
    }
    UNUserNotificationCenter *center = [UNUserNotificationCenter currentNotificationCenter];
    // 保存现有代理, 实现链式转发
    _foregroundDelegate.originalDelegate = center.delegate;
    center.delegate = _foregroundDelegate;
}

@end

#pragma mark - C 函数导出 (供 Unity P/Invoke 调用)

#ifdef __cplusplus
extern "C" {
#endif

void ToolKit_Download_RequestNotificationPermission() {
    [DownloadBridge requestNotificationPermission];
}

void ToolKit_Download_BeginBackgroundTask() {
    [DownloadBridge beginBackgroundTask];
}

void ToolKit_Download_EndBackgroundTask() {
    [DownloadBridge endBackgroundTask];
}

void ToolKit_Download_ShowNotification(const char *title, const char *content, float progress) {
    NSString *nsTitle = title ? [NSString stringWithUTF8String:title] : nil;
    NSString *nsContent = content ? [NSString stringWithUTF8String:content] : nil;
    [DownloadBridge showNotificationWithTitle:nsTitle content:nsContent progress:progress];
}

void ToolKit_Download_HideNotification() {
    [DownloadBridge hideNotification];
}

void ToolKit_Download_EnableForegroundNotification() {
    [DownloadBridge enableForegroundNotification];
}

#ifdef __cplusplus
}
#endif
