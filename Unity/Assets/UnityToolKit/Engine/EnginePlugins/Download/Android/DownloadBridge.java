package com.toolkit.download;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.content.Context;
import android.content.Intent;
import android.os.Build;

/**
 * Android 后台下载桥接类
 * <p>
 * 供 Unity C# 端通过 AndroidJavaClass 调用, 控制前台服务的生命周期和通知更新。
 * 同时提供独立通知功能 (用于前台 DisplayMode.Always 场景)。
 * </p>
 */
public class DownloadBridge {

    static final String CHANNEL_ID = "toolkit_download_channel";
    private static final int STANDALONE_NOTIFICATION_ID = 19901;

    private static Context _context;
    private static NotificationManager _notificationManager;
    private static String _smallIconName;

    /**
     * 初始化 (由 C# 端调用, 传入 UnityPlayer.currentActivity)
     */
    public static void init(Context context) {
        _context = context.getApplicationContext();
        _notificationManager = (NotificationManager)
                _context.getSystemService(Context.NOTIFICATION_SERVICE);
        _createNotificationChannel();
    }

    /**
     * 设置通知小图标资源名 (drawable 资源名, 如 "ic_download")
     */
    public static void setSmallIconName(String iconName) {
        _smallIconName = iconName;
    }

    /**
     * 获取通知小图标资源名
     */
    public static String getSmallIconName() {
        return _smallIconName;
    }

    // ---- 前台服务 ----

    /**
     * 启动前台下载服务
     */
    public static void startService(String title, String content) {
        if (_context == null) return;

        Intent intent = new Intent(_context, DownloadForegroundService.class);
        intent.putExtra("action", "start");
        intent.putExtra("title", title);
        intent.putExtra("content", content);

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            _context.startForegroundService(intent);
        } else {
            _context.startService(intent);
        }
    }

    /**
     * 更新前台服务通知栏进度
     *
     * @param title    通知标题
     * @param content  通知内容
     * @param progress 进度 (0~100)
     */
    public static void updateNotification(String title, String content, int progress) {
        if (_context == null) return;

        Intent intent = new Intent(_context, DownloadForegroundService.class);
        intent.putExtra("action", "update");
        intent.putExtra("title", title);
        intent.putExtra("content", content);
        intent.putExtra("progress", progress);

        _context.startService(intent);
    }

    /**
     * 停止前台下载服务
     */
    public static void stopService() {
        if (_context == null) return;

        Intent intent = new Intent(_context, DownloadForegroundService.class);
        intent.putExtra("action", "stop");
        _context.startService(intent);
    }

    // ---- 独立通知 (前台 DisplayMode.Always 场景) ----

    /**
     * 显示/更新独立通知 (不依赖前台服务)
     * <p>用于 App 在前台时显示下载进度通知 (DisplayMode.Always)</p>
     *
     * @param title    通知标题
     * @param content  通知内容
     * @param progress 进度 (0~100)
     */
    public static void showNotification(String title, String content, int progress) {
        if (_context == null || _notificationManager == null) return;

        Notification.Builder builder;
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            builder = new Notification.Builder(_context, CHANNEL_ID);
        } else {
            builder = new Notification.Builder(_context);
        }

        builder.setContentTitle(title)
                .setContentText(content)
                .setSmallIcon(resolveSmallIcon())
                .setOngoing(true)
                .setProgress(100, Math.min(progress, 100), false);

        // 点击通知打开 App
        PendingIntent contentIntent = createContentIntent();
        if (contentIntent != null) {
            builder.setContentIntent(contentIntent);
        }

        _notificationManager.notify(STANDALONE_NOTIFICATION_ID, builder.build());
    }

    /**
     * 隐藏独立通知
     */
    public static void hideNotification() {
        if (_notificationManager == null) return;
        _notificationManager.cancel(STANDALONE_NOTIFICATION_ID);
    }

    // ---- 辅助方法 ----

    /**
     * 解析通知小图标资源 ID
     * <p>优先使用自定义图标名, 回退到系统默认下载图标</p>
     */
    static int resolveSmallIcon() {
        if (_smallIconName != null && _context != null) {
            int resId = _context.getResources().getIdentifier(
                    _smallIconName, "drawable", _context.getPackageName());
            if (resId != 0) return resId;
        }
        return android.R.drawable.stat_sys_download;
    }

    /**
     * 创建点击通知时打开 App 的 PendingIntent
     */
    static PendingIntent createContentIntent() {
        if (_context == null) return null;

        Intent launchIntent = _context.getPackageManager()
                .getLaunchIntentForPackage(_context.getPackageName());
        if (launchIntent == null) return null;

        launchIntent.addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP);

        int flags = PendingIntent.FLAG_UPDATE_CURRENT;
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
            flags |= PendingIntent.FLAG_IMMUTABLE;
        }
        return PendingIntent.getActivity(_context, 0, launchIntent, flags);
    }

    /**
     * 创建通知渠道 (Android 8.0+)
     */
    private static void _createNotificationChannel() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            NotificationChannel channel = new NotificationChannel(
                    CHANNEL_ID,
                    "下载服务",
                    NotificationManager.IMPORTANCE_LOW
            );
            channel.setDescription("文件下载进度通知");
            channel.setSound(null, null);
            channel.enableVibration(false);
            _notificationManager.createNotificationChannel(channel);
        }
    }
}
