package com.toolkit.imagepicker;

import android.app.Activity;
import android.content.Context;
import android.content.Intent;
import android.util.Log;

import com.unity3d.player.UnityPlayer;

/**
 * Android 图片选择器桥接类
 * <p>
 * 供 Unity C# 端通过 AndroidJavaClass 调用, 控制图片选择流程。
 * 权限检查和请求由 {@link ImagePickerActivity} 统一处理。
 * 实际的选图/拍照逻辑在 {@link ImagePickerActivity} 中处理。
 * </p>
 */
public class ImagePickerBridge {

    private static final String TAG = "ToolKit.ImagePicker";

    private static Context _context;
    private static String _unityGameObjectName;

    /**
     * 初始化 (由 C# 端调用)
     *
     * @param context         UnityPlayer.currentActivity
     * @param gameObjectName  Unity 中接收回调的 GameObject 名称
     */
    public static void init(Context context, String gameObjectName) {
        _context = context.getApplicationContext();
        _unityGameObjectName = gameObjectName;
    }

    /**
     * 开始选图/拍照流程
     * <p>启动透明 Activity, 由其内部统一处理权限请求和相机/图库调用</p>
     *
     * @param configJson JSON 配置字符串 (包含 source, crop 等设置, 压缩在 Unity 侧处理)
     */
    public static void pickImage(String configJson) {
        if (_context == null) {
            Log.e(TAG, "未初始化, 请先调用 init()");
            sendToUnity("OnImagePickerFailed", "90");
            return;
        }

        Activity currentActivity = UnityPlayer.currentActivity;
        if (currentActivity == null) {
            sendToUnity("OnImagePickerFailed", "99|currentActivity is null");
            return;
        }

        // 直接启动 ImagePickerActivity, 由其内部统一处理权限
        Intent intent = new Intent(currentActivity, ImagePickerActivity.class);
        intent.putExtra("config", configJson);
        currentActivity.startActivity(intent);
    }

    /**
     * 发送消息给 Unity (UnitySendMessage)
     *
     * @param method  Unity 端 MonoBehaviour 方法名
     * @param message 消息内容
     */
    static void sendToUnity(String method, String message) {
        if (_unityGameObjectName == null || _unityGameObjectName.isEmpty()) {
            Log.e(TAG, "Unity GameObject 名称未设置");
            return;
        }
        try {
            UnityPlayer.UnitySendMessage(_unityGameObjectName, method, message);
        } catch (Exception e) {
            Log.e(TAG, "UnitySendMessage 失败: " + e.getMessage());
        }
    }
}
