﻿using System;

namespace Marvin.Runtime.Modules
{
    /// <summary>
    /// HealthState notifiable interface for facades
    /// </summary>
    public interface ILifeCycleBoundFacade
    {
        /// <summary>
        /// Activation state of the facade
        /// </summary>
        bool IsActivated { get; }

        /// <summary>
        /// Facade was activated
        /// </summary>
        void Activated();

        /// <summary>
        /// Facade was deactivated
        /// </summary>
        void Deactivated();

        /// <summary>
        /// Event is called when a Activated changed occurs
        /// </summary>
        event EventHandler<bool> StateChanged;
    }
}
