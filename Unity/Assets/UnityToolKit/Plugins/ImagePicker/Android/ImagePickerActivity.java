package com.toolkit.imagepicker;

import android.Manifest;
import android.app.Activity;
import android.content.ContentResolver;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.database.Cursor;
import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.graphics.Matrix;
import android.media.ExifInterface;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.provider.MediaStore;
import android.util.Log;

import androidx.annotation.NonNull;
import androidx.core.content.FileProvider;

import org.json.JSONObject;

import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileOutputStream;
import java.io.InputStream;

/**
 * 图片选择/拍照处理 Activity
 * <p>
 * 透明 Activity, 统一处理权限请求 → 打开相机/图库 → (可选) uCrop 裁剪 → 返回结果给 Unity。
 * 压缩功能已移至 Unity C# 侧统一处理, 保证跨平台一致性。
 * </p>
 */
public class ImagePickerActivity extends Activity {

    private static final String TAG = "ToolKit.ImagePicker";

    private static final int REQUEST_CAMERA = 1001;
    private static final int REQUEST_GALLERY = 1002;
    private static final int REQUEST_CROP = 1003;

    private static final int PERMISSION_REQUEST_CAMERA = 2001;
    private static final int PERMISSION_REQUEST_STORAGE = 2002;

    private JSONObject _config;
    private Uri _cameraOutputUri;
    private File _cameraOutputFile;
    private boolean _isWaitingForPermission;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);

        try {
            String configJson = getIntent().getStringExtra("config");
            _config = new JSONObject(configJson);

            int source = _config.optInt("source", 0);
            if (source == 1) {
                checkAndRequestCameraPermission();
            } else {
                checkAndRequestStoragePermission();
            }
        } catch (Exception e) {
            Log.e(TAG, "onCreate error", e);
            sendFailed(91, e.getMessage());
        }
    }

    // ---- 权限处理 ----

    /**
     * 检查并请求相机权限
     */
    private void checkAndRequestCameraPermission() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
            if (checkSelfPermission(Manifest.permission.CAMERA)
                    != PackageManager.PERMISSION_GRANTED) {
                _isWaitingForPermission = true;
                requestPermissions(
                        new String[]{Manifest.permission.CAMERA},
                        PERMISSION_REQUEST_CAMERA);
                return;
            }
        }
        openCamera();
    }

    /**
     * 检查并请求存储/图库权限
     */
    private void checkAndRequestStoragePermission() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            // Android 13+: READ_MEDIA_IMAGES
            if (checkSelfPermission(Manifest.permission.READ_MEDIA_IMAGES)
                    != PackageManager.PERMISSION_GRANTED) {
                _isWaitingForPermission = true;
                requestPermissions(
                        new String[]{Manifest.permission.READ_MEDIA_IMAGES},
                        PERMISSION_REQUEST_STORAGE);
                return;
            }
        } else if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
            // Android 6.0~12: READ_EXTERNAL_STORAGE
            if (checkSelfPermission(Manifest.permission.READ_EXTERNAL_STORAGE)
                    != PackageManager.PERMISSION_GRANTED) {
                _isWaitingForPermission = true;
                requestPermissions(
                        new String[]{Manifest.permission.READ_EXTERNAL_STORAGE},
                        PERMISSION_REQUEST_STORAGE);
                return;
            }
        }
        openGallery();
    }

    @Override
    public void onRequestPermissionsResult(int requestCode, @NonNull String[] permissions,
                                           @NonNull int[] grantResults) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults);
        _isWaitingForPermission = false;

        boolean granted = grantResults.length > 0
                && grantResults[0] == PackageManager.PERMISSION_GRANTED;

        switch (requestCode) {
            case PERMISSION_REQUEST_CAMERA:
                if (granted) {
                    openCamera();
                } else {
                    sendFailed(10, null);
                }
                break;
            case PERMISSION_REQUEST_STORAGE:
                if (granted) {
                    openGallery();
                } else {
                    sendFailed(11, null);
                }
                break;
        }
    }

    // ---- 打开相机/图库 ----

    private void openCamera() {
        try {
            Intent intent = new Intent(MediaStore.ACTION_IMAGE_CAPTURE);
            if (intent.resolveActivity(getPackageManager()) == null) {
                sendFailed(20, null);
                return;
            }

            // 创建临时文件存储拍照结果
            File dir = new File(getCacheDir(), "ImagePicker");
            if (!dir.exists()) dir.mkdirs();
            _cameraOutputFile = new File(dir, "camera_" + System.currentTimeMillis() + ".jpg");

            // Android 7.0+ 使用 FileProvider
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.N) {
                _cameraOutputUri = FileProvider.getUriForFile(this,
                        getPackageName() + ".toolkit.fileprovider", _cameraOutputFile);
            } else {
                _cameraOutputUri = Uri.fromFile(_cameraOutputFile);
            }

            intent.putExtra(MediaStore.EXTRA_OUTPUT, _cameraOutputUri);
            intent.addFlags(Intent.FLAG_GRANT_WRITE_URI_PERMISSION);
            startActivityForResult(intent, REQUEST_CAMERA);
        } catch (Exception e) {
            Log.e(TAG, "openCamera error", e);
            sendFailed(33, e.getMessage());
        }
    }

    private void openGallery() {
        try {
            Intent intent = new Intent(Intent.ACTION_PICK,
                    MediaStore.Images.Media.EXTERNAL_CONTENT_URI);
            intent.setType("image/*");
            startActivityForResult(intent, REQUEST_GALLERY);
        } catch (Exception e) {
            Log.e(TAG, "openGallery error", e);
            sendFailed(33, e.getMessage());
        }
    }

    // ---- Activity Result ----

    @Override
    protected void onActivityResult(int requestCode, int resultCode, Intent data) {
        super.onActivityResult(requestCode, resultCode, data);

        if (resultCode == RESULT_CANCELED) {
            sendCancelled();
            return;
        }

        if (resultCode != RESULT_OK) {
            sendFailed(33, "resultCode=" + resultCode);
            return;
        }

        try {
            switch (requestCode) {
                case REQUEST_CAMERA:
                    handleCameraResult();
                    break;
                case REQUEST_GALLERY:
                    handleGalleryResult(data);
                    break;
                case REQUEST_CROP:
                    handleCropResult(data);
                    break;
                default:
                    sendFailed(99, "requestCode=" + requestCode);
                    break;
            }
        } catch (Exception e) {
            Log.e(TAG, "onActivityResult error", e);
            sendFailed(33, e.getMessage());
        }
    }

    private void handleCameraResult() throws Exception {
        if (_cameraOutputFile == null || !_cameraOutputFile.exists()) {
            sendFailed(31, null);
            return;
        }
        processImage(Uri.fromFile(_cameraOutputFile));
    }

    private void handleGalleryResult(Intent data) throws Exception {
        if (data == null || data.getData() == null) {
            sendFailed(31, null);
            return;
        }
        processImage(data.getData());
    }

    private void handleCropResult(Intent data) throws Exception {
        // uCrop 将裁剪结果保存到指定的输出 Uri
        Uri resultUri = null;

        // 尝试从 uCrop 结果获取
        try {
            Class<?> uCropClass = Class.forName("com.yalantis.ucrop.UCrop");
            java.lang.reflect.Method getOutput = uCropClass.getMethod("getOutput", Intent.class);
            resultUri = (Uri) getOutput.invoke(null, data);
        } catch (Exception e) {
            Log.w(TAG, "获取 uCrop 结果失败, 尝试从 data 获取", e);
            if (data != null) {
                resultUri = data.getData();
            }
        }

        if (resultUri == null) {
            sendFailed(50, null);
            return;
        }

        // 裁剪后保存返回
        saveAndReturn(resultUri);
    }

    // ---- 图片处理流程 ----

    /**
     * 处理选择/拍照的图片: 约束校验 → 裁剪 → 压缩 → 返回
     */
    private void processImage(Uri imageUri) throws Exception {
        // 1. 解码图片尺寸 (不加载完整图片)
        BitmapFactory.Options opts = new BitmapFactory.Options();
        opts.inJustDecodeBounds = true;

        InputStream is1 = getContentResolver().openInputStream(imageUri);
        BitmapFactory.decodeStream(is1, null, opts);
        if (is1 != null) is1.close();

        int width = opts.outWidth;
        int height = opts.outHeight;

        // 获取文件大小
        long fileSize = getFileSize(imageUri);

        // 2. 约束校验
        String validationError = validateConstraints(width, height, fileSize);
        if (validationError != null) {
            sendFailed(40, validationError);
            return;
        }

        // 3. 裁剪
        boolean enableCrop = _config.optBoolean("enableCrop", false);
        if (enableCrop) {
            startCrop(imageUri);
            return; // 裁剪完成后在 onActivityResult 中继续处理
        }

        // 4. 保存并返回 (压缩在 Unity C# 侧统一处理)
        saveAndReturn(imageUri);
    }

    /**
     * 约束校验
     */
    private String validateConstraints(int width, int height, long fileSize) {
        long maxFileSize = _config.optLong("maxFileSize", 0);
        int minWidth = _config.optInt("minWidth", 0);
        int minHeight = _config.optInt("minHeight", 0);
        int maxWidth = _config.optInt("maxWidth", 0);
        int maxHeight = _config.optInt("maxHeight", 0);

        if (maxFileSize > 0 && fileSize > maxFileSize)
            return "文件大小 (" + fileSize + " bytes) 超过限制 (" + maxFileSize + " bytes)";

        if (minWidth > 0 && width < minWidth)
            return "图片宽度 (" + width + "px) 小于最小限制 (" + minWidth + "px)";

        if (minHeight > 0 && height < minHeight)
            return "图片高度 (" + height + "px) 小于最小限制 (" + minHeight + "px)";

        if (maxWidth > 0 && width > maxWidth)
            return "图片宽度 (" + width + "px) 超过最大限制 (" + maxWidth + "px)";

        if (maxHeight > 0 && height > maxHeight)
            return "图片高度 (" + height + "px) 超过最大限制 (" + maxHeight + "px)";

        return null;
    }

    /**
     * 启动 uCrop 裁剪
     * <p>如果 uCrop 库不可用, 跳过裁剪直接压缩</p>
     */
    private void startCrop(Uri sourceUri) {
        try {
            // 创建输出文件
            File dir = new File(getCacheDir(), "ImagePicker");
            if (!dir.exists()) dir.mkdirs();
            File outputFile = new File(dir, "cropped_" + System.currentTimeMillis() + ".jpg");
            Uri outputUri = Uri.fromFile(outputFile);

            // 通过反射调用 uCrop, 避免硬依赖
            Class<?> uCropClass = Class.forName("com.yalantis.ucrop.UCrop");
            Object uCropBuilder = uCropClass.getMethod("of", Uri.class, Uri.class)
                    .invoke(null, sourceUri, outputUri);
            Class<?> builderClass = uCropBuilder.getClass();

            // 配置裁剪选项
            Class<?> optionsClass = Class.forName("com.yalantis.ucrop.UCrop$Options");
            Object options = optionsClass.newInstance();

            // 裁剪形状
            int cropShape = _config.optInt("cropShape", 0);
            if (cropShape == 1) { // Circle
                optionsClass.getMethod("setCircleDimmedLayer", boolean.class)
                        .invoke(options, true);
            }

            // 输出尺寸限制
            int maxOutputWidth = _config.optInt("maxOutputWidth", 0);
            int maxOutputHeight = _config.optInt("maxOutputHeight", 0);
            if (maxOutputWidth > 0 && maxOutputHeight > 0) {
                builderClass.getMethod("withMaxResultSize", int.class, int.class)
                        .invoke(uCropBuilder, maxOutputWidth, maxOutputHeight);
            }

            // 宽高比
            float aspectX = (float) _config.optDouble("aspectRatioX", 0);
            float aspectY = (float) _config.optDouble("aspectRatioY", 0);
            if (aspectX > 0 && aspectY > 0) {
                builderClass.getMethod("withAspectRatio", float.class, float.class)
                        .invoke(uCropBuilder, aspectX, aspectY);
            }

            // 应用选项并启动
            builderClass.getMethod("withOptions",
                    Class.forName("com.yalantis.ucrop.UCrop$Options"))
                    .invoke(uCropBuilder, options);

            // 获取 Intent 并启动
            Intent cropIntent = (Intent) builderClass.getMethod("getIntent", Activity.class)
                    .invoke(uCropBuilder, this);
            startActivityForResult(cropIntent, REQUEST_CROP);

        } catch (ClassNotFoundException e) {
            Log.w(TAG, "uCrop 库不可用, 跳过裁剪");
            // uCrop 不可用时直接保存返回
            try {
                saveAndReturn(sourceUri);
            } catch (Exception ex) {
                sendFailed(33, ex.getMessage());
            }
        } catch (Exception e) {
            Log.e(TAG, "启动裁剪失败", e);
            try {
                saveAndReturn(sourceUri);
            } catch (Exception ex) {
                sendFailed(33, ex.getMessage());
            }
        }
    }

    /**
     * 保存图片并返回结果给 Unity (不做压缩, 压缩在 Unity C# 侧统一处理)
     * <p>会读取 EXIF 旋转信息并应用到像素数据, 避免 Unity 渲染时方向错误</p>
     */
    private void saveAndReturn(Uri imageUri) throws Exception {
        // 1. 解码图片
        InputStream is2 = getContentResolver().openInputStream(imageUri);
        Bitmap bitmap = BitmapFactory.decodeStream(is2);
        if (is2 != null) is2.close();

        if (bitmap == null) {
            sendFailed(30, null);
            return;
        }

        // 2. 读取 EXIF 旋转信息并应用
        int rotation = getExifRotation(imageUri);
        if (rotation != 0) {
            bitmap = applyRotation(bitmap, rotation);
        }

        int width = bitmap.getWidth();
        int height = bitmap.getHeight();

        // 3. 以高质量 JPEG 保存到临时文件, 压缩由 Unity C# 侧统一处理
        byte[] jpgData = compressToJpeg(bitmap, 95);
        bitmap.recycle();

        File resultFile = saveToFile(jpgData);
        sendSuccess(resultFile.getAbsolutePath(), width, height, jpgData.length);
    }

    // ---- 工具方法 ----

    /**
     * 从 Uri 读取 EXIF 旋转角度
     * <p>相机拍摄的照片在 EXIF 中记录了设备方向, 但 BitmapFactory 解码时不会自动应用</p>
     *
     * @return 旋转角度 (0, 90, 180, 270)
     */
    private int getExifRotation(Uri uri) {
        try {
            ExifInterface exif;
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.N) {
                // Android 7.0+ 支持从 InputStream 读取 EXIF
                InputStream exifStream = getContentResolver().openInputStream(uri);
                if (exifStream == null) return 0;
                exif = new ExifInterface(exifStream);
                exifStream.close();
            } else {
                // 低版本仅支持文件路径
                String path = uri.getPath();
                if (path == null) return 0;
                exif = new ExifInterface(path);
            }

            int orientation = exif.getAttributeInt(
                    ExifInterface.TAG_ORIENTATION, ExifInterface.ORIENTATION_NORMAL);

            switch (orientation) {
                case ExifInterface.ORIENTATION_ROTATE_90:  return 90;
                case ExifInterface.ORIENTATION_ROTATE_180: return 180;
                case ExifInterface.ORIENTATION_ROTATE_270: return 270;
                default: return 0;
            }
        } catch (Exception e) {
            Log.w(TAG, "EXIF 读取失败, 跳过旋转修正", e);
            return 0;
        }
    }

    /**
     * 应用旋转到 Bitmap, 并回收原 Bitmap
     */
    private Bitmap applyRotation(Bitmap source, int degrees) {
        Matrix matrix = new Matrix();
        matrix.postRotate(degrees);
        Bitmap rotated = Bitmap.createBitmap(source, 0, 0,
                source.getWidth(), source.getHeight(), matrix, true);
        if (rotated != source) {
            source.recycle();
        }
        return rotated;
    }

    private byte[] compressToJpeg(Bitmap bitmap, int quality) {
        ByteArrayOutputStream baos = new ByteArrayOutputStream();
        bitmap.compress(Bitmap.CompressFormat.JPEG, quality, baos);
        return baos.toByteArray();
    }

    private File saveToFile(byte[] data) throws Exception {
        File dir = new File(getCacheDir(), "ImagePicker");
        if (!dir.exists()) dir.mkdirs();
        File file = new File(dir, "result_" + System.currentTimeMillis() + ".jpg");
        FileOutputStream fos = new FileOutputStream(file);
        fos.write(data);
        fos.close();
        return file;
    }

    private long getFileSize(Uri uri) {
        try {
            if ("file".equals(uri.getScheme())) {
                File f = new File(uri.getPath());
                return f.length();
            }
            ContentResolver cr = getContentResolver();
            Cursor cursor = cr.query(uri, new String[]{MediaStore.MediaColumns.SIZE},
                    null, null, null);
            if (cursor != null && cursor.moveToFirst()) {
                long size = cursor.getLong(0);
                cursor.close();
                return size;
            }
            // 回退: 读取流
            InputStream is3 = cr.openInputStream(uri);
            if (is3 != null) {
                long size = 0;
                byte[] buf = new byte[8192];
                int n;
                while ((n = is3.read(buf)) != -1) size += n;
                is3.close();
                return size;
            }
        } catch (Exception e) {
            Log.w(TAG, "获取文件大小失败", e);
        }
        return 0;
    }

    // ---- Unity 消息回调 ----

    private void sendSuccess(String filePath, int width, int height, long fileSize) {
        String result = filePath + "|" + width + "|" + height + "|" + fileSize;
        ImagePickerBridge.sendToUnity("OnImagePickerSuccess", result);
        finish();
    }

    private void sendFailed(int code, String detail) {
        String msg = detail != null ? code + "|" + detail : String.valueOf(code);
        ImagePickerBridge.sendToUnity("OnImagePickerFailed", msg);
        finish();
    }

    private void sendCancelled() {
        ImagePickerBridge.sendToUnity("OnImagePickerCancelled", "");
        finish();
    }
}
