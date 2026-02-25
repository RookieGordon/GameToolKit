using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Tests
{
    public class ImagePickTest : MonoBehaviour
    {
        public RawImage DisplayImage;
        public Button TakePhotoBtn;
        public Button PickFromGalleryBtn;

        private void Awake()
        {
            TakePhotoBtn.onClick.AddListener(OnClickTakePhotoBtn);
            PickFromGalleryBtn.onClick.AddListener(OnClickPickFromGalleryBtn);
        }

        void OnClickTakePhotoBtn()
        {
            
        }

        void OnClickPickFromGalleryBtn()
        {
            
        }
    }
}
