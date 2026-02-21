#ifndef VERTEX_ANIMATION_TEXTURE_H
    #define VERTEX_ANIMATION_TEXTURE_H

    TEXTURE2D(_AnimTexture);

    #define TEXELS_PER_BONE 3

    void CalculateFrameValues(float4 positionOS, half3 normal, half2 boneWeights[4], int rowIndex,
    out float4 positionOut, out float3 normalOut)
    {
        positionOut = float4(0, 0, 0, 0);
        normalOut = float3(0, 0, 0);

        for(int i = 0; i < 4; i++)
        {
            half boneWeight = boneWeights[i].y;
            int boneIndex = boneWeights[i].x;
            float4 row0 = _AnimTexture.Load(int3(boneIndex * TEXELS_PER_BONE, rowIndex, 0));
            float4 row1 = _AnimTexture.Load(int3(boneIndex * TEXELS_PER_BONE + 1, rowIndex, 0));
            float4 row2 = _AnimTexture.Load(int3(boneIndex * TEXELS_PER_BONE + 2, rowIndex, 0));
            float4 row3 = float4(0,0,0,1);
            float4x4 animatedBoneMatrix = float4x4(row0, row1, row2, row3);
            positionOut += boneWeight * mul(animatedBoneMatrix, positionOS);
            normalOut += boneWeight * mul((float3x3)animatedBoneMatrix, normal);
        }
    }

    void AnimateBlend(float4 positionOS, half3 normal, half4 boneWeight_0_1, half4 boneWeight_2_3, half4 clipInfo, float playTime, float animSpeed,
    out float4 positionOut, out float3 normalOut)
    {
        positionOut = float4(0, 0, 0, 0);
        normalOut = float3(0, 0, 0);

        half2 boneWeights[4] = 
        {
            boneWeight_0_1.xy,  boneWeight_0_1.zw,
            boneWeight_2_3.xy,  boneWeight_2_3.zw
        };

        float elapsedTime = (_Time.y - playTime) * animSpeed;  //已经播放的时间(s)
        half offsetRows = clipInfo.x;
        float durationTime = clipInfo.y;
        half isLooping = clipInfo.z;
        float fps = clipInfo.w;
        
        float frameIndex = (isLooping == 1.0) ? 
        (elapsedTime * fps) % (durationTime * fps) : 
        (min(elapsedTime, durationTime) * fps);

        float preFrameIndex = floor(frameIndex);
        float nextFrameIndex = ceil(frameIndex);
        float s = frameIndex - preFrameIndex;    //分母 = nextFrameIndex - preFrameIndex = 1

        float4 prePosition, nextPosition;
        float3 preNormal, nextNormal;
        CalculateFrameValues(positionOS, normal, boneWeights, offsetRows + preFrameIndex, prePosition, preNormal);
        CalculateFrameValues(positionOS, normal, boneWeights, offsetRows + nextFrameIndex, nextPosition, nextNormal);

        positionOut = lerp(prePosition, nextPosition, s);
        normalOut = lerp(preNormal, nextNormal, s);

        // positionOut = prePosition;
        // normalOut = preNormal;
    }

    ///////////////////////////////////// 不带法线 //////////////////////////////////////
    void CalculateFrameValues_NoNormal(float4 positionOS, half2 boneWeights[4], int rowIndex,
    out float4 positionOut)
    {
        positionOut = float4(0, 0, 0, 0);

        for(int i = 0; i < 4; i++)
        {
            half boneWeight = boneWeights[i].y;
            int boneIndex = boneWeights[i].x;
            float4 row0 = _AnimTexture.Load(int3(boneIndex * TEXELS_PER_BONE, rowIndex, 0));
            float4 row1 = _AnimTexture.Load(int3(boneIndex * TEXELS_PER_BONE + 1, rowIndex, 0));
            float4 row2 = _AnimTexture.Load(int3(boneIndex * TEXELS_PER_BONE + 2, rowIndex, 0));
            float4 row3 = float4(0,0,0,1);
            float4x4 animatedBoneMatrix = float4x4(row0, row1, row2, row3);
            positionOut += boneWeight * mul(animatedBoneMatrix, positionOS);
        }
    }

    void AnimateBlend_NoNormal(float4 positionOS, half4 boneWeight_0_1, half4 boneWeight_2_3, half4 clipInfo, float playTime, float animSpeed,
    out float4 positionOut)
    {
        positionOut = float4(0, 0, 0, 0);

        half2 boneWeights[4] = 
        {
            boneWeight_0_1.xy,  boneWeight_0_1.zw,
            boneWeight_2_3.xy,  boneWeight_2_3.zw
        };

        float elapsedTime = (_Time.y - playTime) * animSpeed;  //已经播放的时间(s)
        half offsetRows = clipInfo.x;
        float durationTime = clipInfo.y;
        half isLooping = clipInfo.z;
        float fps = clipInfo.w;
        
        float frameIndex = (isLooping == 1.0) ? 
        (elapsedTime * fps) % (durationTime * fps) : 
        (min(elapsedTime, durationTime) * fps);

        float preFrameIndex = floor(frameIndex);
        float nextFrameIndex = ceil(frameIndex);
        float s = frameIndex - preFrameIndex;    //分母 = nextFrameIndex - preFrameIndex = 1

        float4 prePosition, nextPosition;
        CalculateFrameValues_NoNormal(positionOS, boneWeights, offsetRows + preFrameIndex, prePosition);
        CalculateFrameValues_NoNormal(positionOS, boneWeights, offsetRows + nextFrameIndex, nextPosition);

        positionOut = lerp(prePosition, nextPosition, s);

        // positionOut = prePosition;
    }
#endif