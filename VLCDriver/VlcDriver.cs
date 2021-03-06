﻿using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using Castle.MicroKernel.Registration;
using Castle.Windsor;
using NLog;

namespace VLCDriver
{
    public class VlcDriver : IVlcDriver
    {
        public VlcDriver(IVlcStarter starter = null, IPortAllocator allocator = null, IVlcLocator locator = null, ILogger logger = null)
        {
            this.logger = logger;
            this.logger = this.logger ?? LogManager.GetCurrentClassLogger();

            Starter = starter ?? new VlcStarter(this.logger);

            Locator = locator ?? new VlcLocator();

            var portAllocator = allocator ?? new PortAllocator(this.logger){ StartPort = Properties.Settings.Default.StartPort };

            container = new WindsorContainer();
            container.Register(Component.For<VlcAudioJob>().LifestyleTransient());
            container.Register(Component.For<VlcVideoJob>().LifestyleTransient());
            container.Register(Component.For<IAudioConfiguration>().ImplementedBy<AudioConfiguration>().LifestyleTransient());
            container.Register(Component.For<IVideoConfiguration>().ImplementedBy<VideoConfiguration>().LifestyleTransient());

            container.Register(Component.For<IPortAllocator>().Instance(portAllocator));
            container.Register(Component.For<ILogger>().Instance(this.logger));

            container.Register(Component.For<IStatusParser>().ImplementedBy<StatusParser>().LifestyleTransient());
            container.Register(Component.For<IVlcStatusSource>().ImplementedBy<HttpVlcStatusSource>().LifestyleTransient());
            container.Register(Component.For<ITimeSouce>().ImplementedBy<TimeSouce>().LifestyleTransient());

            LogCurrentAssemblyInfo();
        }

        private void LogCurrentAssemblyInfo()
        {
            var assembly =  System.Reflection.Assembly.GetExecutingAssembly();
            var version = assembly.GetName().Version.ToString();
            logger.Debug("VLC Driver Instance created. Version {0}", version);
        }

        private IVlcStarter Starter { get; set; }

        private readonly WindsorContainer container;
        private readonly ILogger logger;

        public IVlcLocator Locator { get; protected set; }

        public FileInfo VlcExePath
        {
            get
            {
                if (vlcExePath != null)
                {
                    return vlcExePath;
                }
                if (Locator == null || Locator.Location == null)
                {
                    var invalidOperationException = new InvalidOperationException("VLC Cannot be automatically located on this system. Please set the 'VlcExePath' field to the path of the VLC executable");
                    logger.Error(invalidOperationException);
                    throw invalidOperationException;
                }
                return vlcExePath = new FileInfo(Locator.Location);
            }
            set
            {
                vlcExePath = value;
            }
        }
        private FileInfo vlcExePath;

        public IVlcInstance StartInstance(string parameters = "")
        {
            return Starter.Start(parameters, VlcExePath);
        }

        public VlcVideoJob CreateVideoJob()
        {
            return container.Resolve<VlcVideoJob>();
        }

        public VlcAudioJob CreateAudioJob()
        {
            return container.Resolve<VlcAudioJob>();
        }

        public void StartJob(VlcJob job)
        {
            logger.Debug("Call to start job. Input file: {0} Output file: {1}", job.InputFile.FullName, job.OutputFile.FullName);
            job.QuitAfer = true; //fairly important if we're tracking it
            var vlcArguments = job.GetVlcArguments();

            job.State = VlcJob.JobState.Started;
            var instance = Starter.Start(vlcArguments, VlcExePath);
            job.Instance = instance;
            instance.OnExited += OnVlcInstanceExited;
            JobBag.Add(job);
        }

        private void OnVlcInstanceExited(object source, EventArgs e)
        {
            var instance = source as IVlcInstance;
            if (instance == null)
            {
                var sourceMustBeAVlcInstance = "Source must be a VLC instance";
                logger.Error(sourceMustBeAVlcInstance);
                throw new InvalidOperationException(sourceMustBeAVlcInstance);
            }
            instance.OnExited -= OnVlcInstanceExited;

            var associatedJob = JobBag.First(x => x.Instance == instance);
            associatedJob.SetJobComplete();
            if (OnJobStateChange != null)
            {
                OnJobStateChange(this, new JobStatusChangedEventArgs { Job = associatedJob });
            }
        }

        public ConcurrentBag<VlcJob> JobBag
        {
            get { return bag ?? (bag = new ConcurrentBag<VlcJob>()); }
        }
        private ConcurrentBag<VlcJob> bag;

        public delegate void VlcDriverEventHandler(object source,JobStatusChangedEventArgs e);
        public event VlcDriverEventHandler OnJobStateChange;
    }
}
