﻿using StagWare.FanControl.Configurations;
using StagWare.FanControl.Plugins;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;

namespace StagWare.FanControl
{
    public class FanControl : IDisposable
    {
        #region Constants

        private const int EcTimeout = 200;
        private const int MaxWaitHandleTimeout = 500;
        private const int MinPollInterval = 100;
        private const int DefaultPollInterval = 3000;
        private const string PluginPath = "Plugins";
        public const int AutoFanSpeedPercentage = 101;

        #endregion

        #region Private Fields

        private AutoResetEvent autoEvent = new AutoResetEvent(false);
        private Timer timer;
        private AsyncOperation asyncOp;

        private readonly int pollInterval;
        private readonly int waitHandleTimeout;
        private readonly FanControlConfigV2 config;

        private readonly ITemperatureFilter tempFilter;
        private readonly ITemperatureProvider tempProvider;
        private readonly IEmbeddedController ec;
        private readonly FanSpeedManager[] fanSpeedManagers;

        private FanInformation[] fanInfo;
        private FanInformation[] fanInfoInternal;

        private volatile float cpuTemperature;
        private volatile float[] requestedSpeeds;

        #endregion

        #region Constructor

        public FanControl(FanControlConfigV2 config)
            : this(PluginPath, config)
        {
        }

        public FanControl(string pluginsPath, FanControlConfigV2 config) :
            this(
             config,
             new ArithmeticMeanTemperatureFilter(DeliminatePollInterval(config.EcPollInterval)),
             LoadTempProviderPlugin(pluginsPath),
             LoadEcPlugin(pluginsPath))
        { }

        public FanControl(
            FanControlConfigV2 config,
            ITemperatureFilter tempFilter,
            ITemperatureProvider tempProvider,
            IEmbeddedController ec)
        {
            if (config == null)
            {
                throw new ArgumentNullException("config");
            }

            if (tempFilter == null)
            {
                throw new ArgumentNullException("filter");
            }

            if (tempProvider == null)
            {
                throw new ArgumentNullException("tempProvider");
            }

            if (ec == null)
            {
                throw new ArgumentNullException("ec");
            }

            this.tempFilter = tempFilter;
            this.tempProvider = tempProvider;
            this.ec = ec;
            this.config = (FanControlConfigV2)config.Clone();
            this.pollInterval = config.EcPollInterval;
            this.waitHandleTimeout = Math.Min(MaxWaitHandleTimeout, config.EcPollInterval);
            this.requestedSpeeds = new float[config.FanConfigurations.Count];
            this.fanInfo = new FanInformation[config.FanConfigurations.Count];
            this.fanInfoInternal = new FanInformation[config.FanConfigurations.Count];
            this.fanSpeedManagers = new FanSpeedManager[config.FanConfigurations.Count];
            this.asyncOp = AsyncOperationManager.CreateOperation(null);

            for (int i = 0; i < config.FanConfigurations.Count; i++)
            {
                var cfg = config.FanConfigurations[i];

                this.fanSpeedManagers[i] = new FanSpeedManager(cfg, config.CriticalTemperature);
                this.requestedSpeeds[i] = AutoFanSpeedPercentage;
                this.fanInfo[i] = new FanInformation(0, 0, true, false, cfg.FanDisplayName);
            }
        }

        #region Contruction Helper Methods

        private static int DeliminatePollInterval(int pollInterval)
        {
            #region limit poll intervall in release

#if !DEBUG

            if (pollInterval < MinPollInterval)
            {
                return DefaultPollInterval;
            }

#endif

            #endregion

            return pollInterval;
        }

        private static IEmbeddedController LoadEcPlugin(string pluginsPath)
        {
            var loader = new FanControlPluginLoader<IEmbeddedController>(pluginsPath);
            return loader.FanControlPlugin;
        }

        private static ITemperatureProvider LoadTempProviderPlugin(string pluginsPath)
        {
            var loader = new FanControlPluginLoader<ITemperatureProvider>(pluginsPath);
            return loader.FanControlPlugin;
        }

        #endregion

        #endregion

        #region Events

        public event EventHandler EcUpdated;

        #endregion

        #region Properties

        public float CpuTemperature
        {
            get { return this.cpuTemperature; }
        }

        public bool Enabled
        {
            get { return this.timer != null; }
        }

        public ReadOnlyCollection<FanInformation> FanInformation
        {
            get
            {
                return Array.AsReadOnly(fanInfo);
            }
        }

        #endregion

        #region Public Methods

        public void Start(int delay = 0)
        {
            if (this.disposed)
            {
                throw new ObjectDisposedException(null);
            }

            if (!this.Enabled)
            {
                if (!this.tempProvider.IsInitialized)
                {
                    this.tempProvider.Initialize();
                }

                if (!this.ec.IsInitialized)
                {
                    this.ec.Initialize();
                }

                this.autoEvent.Set();
                InitializeRegisterWriteConfigurations();

                if (this.timer == null)
                {
                    this.timer = new Timer(new TimerCallback(TimerCallback), null, delay, this.pollInterval);
                }
            }
        }

        public void SetTargetFanSpeed(float speed, int fanIndex)
        {
            if (fanIndex >= 0 && fanIndex < this.requestedSpeeds.Length)
            {
                Thread.VolatileWrite(ref this.requestedSpeeds[fanIndex], speed);
                ThreadPool.QueueUserWorkItem(TimerCallback, null);
            }
            else
            {
                this.Dispose();
                throw new IndexOutOfRangeException("fanIndex");
            }
        }

        public void Stop()
        {
            if (this.disposed)
            {
                throw new ObjectDisposedException(null);
            }
            else
            {
                StopFanControlCore();
            }
        }

        #endregion

        #region Protected Methods

        protected void OnEcUpdated()
        {
            if (EcUpdated != null)
            {
                EcUpdated(this, new EventArgs());
            }
        }

        #endregion

        #region Private Methods

        #region Update EC

        private void TimerCallback(object state)
        {
            if (this.autoEvent.WaitOne(this.waitHandleTimeout))
            {
                try
                {
                    // Read CPU temperature before the call to UpdateEc(),
                    // because both methods try to aquire ISA bus mutex
                    this.cpuTemperature = (float)GetTemperature();
                    UpdateEc();
                }
                finally
                {
                    this.autoEvent.Set();
                }
            }
        }

        private void UpdateEc()
        {
            if (this.ec.AquireLock(EcTimeout))
            {
                try
                {
                    ApplyRegisterWriteConfigurations();
                    UpdateAndApplyFanSpeeds();
                }
                finally
                {
                    this.ec.ReleaseLock();
                }

                asyncOp.Post((object args) => OnEcUpdated(), null);
            }
        }

        private void StopFanControlCore()
        {
            if (this.Enabled)
            {
                if (this.autoEvent != null && !this.autoEvent.SafeWaitHandle.IsClosed)
                {
                    this.autoEvent.Reset();
                }

                if (timer != null)
                {
                    using (var handle = new EventWaitHandle(false, EventResetMode.ManualReset))
                    {
                        timer.Dispose(handle);

                        if (handle.WaitOne())
                        {
                            timer = null;
                        }
                    }
                }

                ResetEc();
            }
        }

        private void UpdateAndApplyFanSpeeds()
        {
            for (int i = 0; i < this.fanSpeedManagers.Length; i++)
            {
                float speed = Thread.VolatileRead(ref this.requestedSpeeds[i]);
                this.fanSpeedManagers[i].UpdateFanSpeed(speed, this.cpuTemperature);

                WriteValue(
                    this.config.FanConfigurations[i].WriteRegister,
                    this.fanSpeedManagers[i].FanSpeedValue,
                    this.config.ReadWriteWords);
            }

            UpdateFanInformation();
        }

        private void UpdateFanInformation()
        {
            for (int i = 0; i < this.fanSpeedManagers.Length; i++)
            {
                FanSpeedManager speedMan = this.fanSpeedManagers[i];
                FanConfiguration cfg = this.config.FanConfigurations[i];
                int speedVal = GetFanSpeedValue(cfg, this.config.ReadWriteWords);

                this.fanInfoInternal[i] = new FanInformation(
                    speedMan.FanSpeedPercentage,
                    speedMan.FanSpeedToPercentage(speedVal),
                    speedMan.AutoControlEnabled,
                    speedMan.CriticalModeEnabled,
                    cfg.FanDisplayName);
            }

            this.fanInfoInternal = Interlocked.Exchange(ref this.fanInfo, this.fanInfoInternal);
        }

        private void InitializeRegisterWriteConfigurations()
        {
            try
            {
                if (this.autoEvent.WaitOne(waitHandleTimeout) && this.ec.AquireLock(EcTimeout))
                {
                    try
                    {
                        ApplyRegisterWriteConfigurations(true);
                    }
                    finally
                    {
                        this.ec.ReleaseLock();
                    }
                }
                else
                {
                    throw new TimeoutException("EC initialization timed out.");
                }
            }
            finally
            {
                this.autoEvent.Set();
            }
        }

        private void ApplyRegisterWriteConfigurations(bool initializing = false)
        {
            if (this.config.RegisterWriteConfigurations != null)
            {
                foreach (RegisterWriteConfiguration cfg in this.config.RegisterWriteConfigurations)
                {
                    if (initializing || cfg.WriteOccasion == RegisterWriteOccasion.OnWriteFanSpeed)
                    {
                        ApplyRegisterWriteConfig(cfg.Value, cfg.Register, cfg.WriteMode);
                    }
                }
            }
        }

        private void ApplyRegisterWriteConfig(int value, int register, RegisterWriteMode mode)
        {
            if (mode == RegisterWriteMode.And)
            {
                value &= ReadValue(register, config.ReadWriteWords);
            }
            else if (mode == RegisterWriteMode.Or)
            {
                value |= ReadValue(register, config.ReadWriteWords);
            }

            WriteValue(register, value, config.ReadWriteWords);
        }

        #endregion

        #region R/W EC

        private void WriteValue(int register, int value, bool writeWord)
        {
            if (writeWord)
            {
                this.ec.WriteWord((byte)register, (ushort)value);
            }
            else
            {
                this.ec.WriteByte((byte)register, (byte)value);
            }
        }

        private int ReadValue(int register, bool readWord)
        {
            return readWord
                ? this.ec.ReadWord((byte)register)
                : this.ec.ReadByte((byte)register);
        }

        #endregion

        #region Reset EC

        private void ResetEc()
        {
            if (this.config.RegisterWriteConfigurations.Any(x => x.ResetRequired)
                || this.config.FanConfigurations.Any(x => x.ResetRequired))
            {
                bool mutexAquired = this.ec.AquireLock(EcTimeout * 2);

                //try to reset the EC even if AquireLock failed
                try
                {
                    if (config != null)
                    {
                        for (int i = 0; i < 3; i++)
                        {
                            ResetRegisterWriteConfigs();
                            ResetFans();
                        }
                    }
                }
                finally
                {
                    if (mutexAquired)
                    {
                        this.ec.ReleaseLock();
                    }
                }
            }
        }

        private void ResetFans()
        {
            foreach (FanConfiguration cfg in this.config.FanConfigurations)
            {
                if (cfg.ResetRequired)
                {
                    WriteValue(cfg.WriteRegister, cfg.FanSpeedResetValue, this.config.ReadWriteWords);
                }
            }
        }

        private void ResetRegisterWriteConfigs()
        {
            foreach (RegisterWriteConfiguration cfg in this.config.RegisterWriteConfigurations)
            {
                if (cfg.ResetRequired)
                {
                    ApplyRegisterWriteConfig(cfg.ResetValue, cfg.Register, cfg.ResetWriteMode);
                }
            }
        }

        #endregion

        #region Get Hardware Infos

        private int GetFanSpeedValue(FanConfiguration cfg, bool readWriteWords)
        {
            int fanSpeed = 0;
            int min = Math.Min(cfg.MinSpeedValue, cfg.MaxSpeedValue);
            int max = Math.Max(cfg.MinSpeedValue, cfg.MaxSpeedValue);

            // If the value is out of range 3 or more times,
            // minFanSpeed and/or maxFanSpeed are probably wrong.
            for (int i = 0; i <= 2; i++)
            {
                fanSpeed = ReadValue(cfg.ReadRegister, readWriteWords);

                if ((fanSpeed >= min) && (fanSpeed <= max))
                {
                    break;
                }
            }

            return fanSpeed;
        }

        private double GetTemperature()
        {
            return this.tempFilter.FilterTemperature(this.tempProvider.GetTemperature());
        }

        #endregion

        #endregion

        #region IDisposable implementation

        private bool disposed = false;

        public void Dispose()
        {
            if (!this.disposed)
            {
                this.disposed = true;
                StopFanControlCore();

                if (this.autoEvent != null)
                {
                    this.autoEvent.Dispose();
                }

                if (this.asyncOp != null)
                {
                    this.asyncOp.OperationCompleted();
                }

                if (this.ec != null)
                {
                    this.ec.Dispose();
                }

                if (this.tempProvider != null)
                {
                    this.tempProvider.Dispose();
                }

                GC.SuppressFinalize(this);
            }
        }

        #endregion
    }
}