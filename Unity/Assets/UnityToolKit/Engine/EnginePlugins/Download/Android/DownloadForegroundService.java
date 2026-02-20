package com.toolkit.download;

import android.app.Notification;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.content.Context;
import android.content.Intent;
import android.os.IBinder;
import android.os.PowerManager;

/**
 * Android 后台下载前台服务
 * <p>
 * 当 Unity 进入后台时, 通过启动前台服务保持进程活跃,
 * 使下载任务不被系统杀死。同时在通知栏显示下载进度。
 * </p>
 * <p>
 * 通知渠道和图标由 {@link DownloadBridge} 统一管理。
 * 最小权限: FOREGROUND_SERVICE, POST_NOTIFICATIONS (Android 13+)
 * </p>
 */
public class DownloadForegroundService extends Service {

    private static final int NOTIFICATION_ID = 19900;

    private NotificationManager _notificationManager;
    private Notification.Builder _notificationBuilder;
    private PowerManager.WakeLock _wakeLock;

    @Override
    public void onCreate() {
        super.onCreate();
        _notificationManager = (NotificationManager) getSystemService(Context.NOTIFICATION_SERVICE);
        _acquireWakeLock();
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        if (intent != null) {
            String action = intent.getStringExtra("action");
            if ("start".equals(action)) {
                String title = intent.getStringExtra("title");
                String content = intent.getStringExtra("content");
                if (title == null) title = "下载中";
                if (content == null) content = "正在下载文件...";
                _startForeground(title, content);
            } else if ("update".equals(action)) {
                String title = intent.getStringExtra("title");
                String content = intent.getStringExtra("content");
                int progress = intent.getIntExtra("progress", 0);
                _updateNotification(title, content, progress);
            } else if ("stop".equals(action)) {
                _stopForegroundService();
            }
        }
        return START_NOT_STICKY;
    }

    @Override
    public IBinder onBind(Intent intent) {
        return null;
    }

    @Override
    public void onDestroy() {
        _releaseWakeLock();
        super.onDestroy();
    }

    /**
     * 启动前台服务并显示通知
     * <p>使用 DownloadBridge 管理的通知渠道、图标和点击意图</p>
     */
    private void _startForeground(String title, String content) {
        if (android.os.Build.VERSION.SDK_INT >= android.os.Build.VERSION_CODES.O) {
            _notificationBuilder = new Notification.Builder(this, DownloadBridge.CHANNEL_ID);
        } else {
            _notificationBuilder = new Notification.Builder(this);
        }

        _notificationBuilder
                .setContentTitle(title)
                .setContentText(content)
                .setSmallIcon(DownloadBridge.resolveSmallIcon())
                .setOngoing(true)
                .setProgress(100, 0, false);

        // 点击通知打开 App
        PendingIntent contentIntent = DownloadBridge.createContentIntent();
        if (contentIntent != null) {
            _notificationBuilder.setContentIntent(contentIntent);
        }

        startForeground(NOTIFICATION_ID, _notificationBuilder.build());
    }

    /**
     * 更新通知栏进度
     */
    private void _updateNotification(String title, String content, int progress) {
        if (_notificationBuilder == null) return;

        if (title != null) _notificationBuilder.setContentTitle(title);
        if (content != null) _notificationBuilder.setContentText(content);
        _notificationBuilder.setProgress(100, Math.min(progress, 100), false);

        _notificationManager.notify(NOTIFICATION_ID, _notificationBuilder.build());
    }

    /**
     * 停止前台服务
     */
    private void _stopForegroundService() {
        stopForeground(true);
        stopSelf();
    }

    /**
     * 获取 Partial WakeLock, 防止 CPU 休眠中断下载
     * <p>最小权限: 仅使用 PARTIAL_WAKE_LOCK, 不影响屏幕和键盘</p>
     */
    private void _acquireWakeLock() {
        PowerManager pm = (PowerManager) getSystemService(Context.POWER_SERVICE);
        if (pm != null) {
            _wakeLock = pm.newWakeLock(
                    PowerManager.PARTIAL_WAKE_LOCK,
                    "ToolKit:DownloadWakeLock"
            );
            _wakeLock.acquire(60 * 60 * 1000L); // 最长 1 小时, 防止永久持有
        }
    }

    /**
     * 释放 WakeLock
     */
    private void _releaseWakeLock() {
        if (_wakeLock != null && _wakeLock.isHeld()) {
            _wakeLock.release();
            _wakeLock = null;
        }
    }
}
