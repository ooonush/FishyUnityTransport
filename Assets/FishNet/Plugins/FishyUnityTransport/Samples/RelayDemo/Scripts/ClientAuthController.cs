using FishNet.Object;
using FishNet.Object.Prediction;
using FishNet.Transporting;
using System;
using System.Collections.Generic;
using FishNet;
using UnityEngine;

namespace FishNet.Example.Transport.UnityTransport.Relay
{
    
    // Very basic client auth controller for demo purposes.
    public class ClientAuthController : NetworkBehaviour
    {

        #region Serialized.

        /// <summary>
        /// How many units to move per second.
        /// </summary>
        [Tooltip("How many units to move per second.")] [SerializeField]
        private float _moveRate = 5f;

        #endregion

        private void Update()
        {
            if (!IsOwner) return;
            
            Vector3 move = new Vector3(Input.GetAxisRaw("Horizontal"), 0f, Input.GetAxisRaw("Vertical"));
            transform.position += (move * _moveRate * (float)base.TimeManager.TickDelta);
        }
    }
}