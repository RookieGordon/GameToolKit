using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Test
{
    public class RandomMove : MonoBehaviour
    {
        private float _moveTime = 0f;
        private float _totalMoveTime = 1f;

        private Vector2 _mixPos = Vector2.one * -100;
        private Vector2 _maxPos = Vector2.one * 100;

        private Vector3 _moveTarget;
        private float _moveSpeed = 1f;
        private bool _canMove = true;
        
        public bool Moved { get; private set; }

        private void Awake()
        {
            _totalMoveTime = UnityEngine.Random.Range(3, 6);
            _moveSpeed = UnityEngine.Random.Range(2, 5);
        }

        public void UpdateMove()
        {
            _RandomResetMoveTarget();

            if (_canMove)
            {
                transform.position = Vector3.Lerp(transform.position, _moveTarget, Time.deltaTime * _moveSpeed);
            }

            Moved = _canMove;
        }

        private void _RandomResetMoveTarget()
        {
            _moveTime += Time.deltaTime;
            if (_moveTime > _totalMoveTime && UnityEngine.Random.Range(0, 101) <= 30)
            {
                _moveTime -= _totalMoveTime;
                _totalMoveTime = UnityEngine.Random.Range(3, 6);
                _moveSpeed = UnityEngine.Random.Range(2, 5);
                _moveTarget = new Vector3(UnityEngine.Random.Range(_mixPos.x, _maxPos.x), 0,
                    UnityEngine.Random.Range(_mixPos.y, _maxPos.y));
                _canMove = UnityEngine.Random.Range(0, 101) > 80;
            }
        }

        public void InitMap(Vector2 mixPos, Vector2 maxPos)
        {
            _mixPos = mixPos;
            _maxPos = maxPos;
        }
    }
}