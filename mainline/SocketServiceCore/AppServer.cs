﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.ServiceModel;
using System.ServiceModel.Channels;
using System.ServiceModel.Description;
using System.Text;
using System.Threading;
using SuperSocket.Common;
using SuperSocket.SocketServiceCore.Command;
using SuperSocket.SocketServiceCore.Config;
using SuperSocket.SocketServiceCore.Security;
using SuperSocket.SocketServiceCore.Protocol;

namespace SuperSocket.SocketServiceCore
{
    public interface IAppServer<T> : IRunable<T>, ICommandSource<T>
         where T : IAppSession, new()
    {
        IServerConfig Config { get; }
        ICommandParser CommandParser { get; }
        X509Certificate Certificate { get; }
        T CreateAppSession(ISocketSession socketSession);
        T GetAppSessionByIndentityKey(string identityKey);
        int SessionCount { get; }
    }

    public abstract class AppServer<T> : IAppServer<T>, IDisposable
        where T : IAppSession, IAppSession<T>, new()
    {
        private IPEndPoint m_LocalEndPoint;

        public IServerConfig Config { get; private set; }

        protected virtual ConsoleHostInfo ConsoleHostInfo { get { return null; } }

        public virtual ICommandParser CommandParser { get; protected set; }

        public virtual ICommandParameterParser CommandParameterParser { get; protected set; }

        public virtual X509Certificate Certificate { get; protected set; }

        public virtual object Protocol { get; protected set; }

        private string m_ConsoleBaseAddress;

        public AppServer()
        {
            
        }

        public ServiceCredentials ServerCredentials { get; set; }

        private Dictionary<string, ICommand<T>> m_CommandDict = new Dictionary<string, ICommand<T>>(StringComparer.OrdinalIgnoreCase);

        private ICommandLoader m_CommandLoader = new ReflectCommandLoader();

        private bool LoadCommands()
        {
            foreach (var command in m_CommandLoader.LoadCommands<T>())
            {
                command.DefaultParameterParser = CommandParameterParser;
                if (m_CommandDict.ContainsKey(command.Name))
                {
                    LogUtil.LogError(this, "Duplicated name command has been found! Command name: " + command.Name);
                    return false;
                }

                m_CommandDict.Add(command.Name, command);
            }

            return true;
        }

        private ServiceHost CreateConsoleHost(ConsoleHostInfo consoleInfo)
        {
            Binding binding = new BasicHttpBinding();

            var host = new ServiceHost(consoleInfo.ServiceInstance, new Uri(m_ConsoleBaseAddress + Name));

            foreach (var contract in consoleInfo.ServiceContracts)
            {
                host.AddServiceEndpoint(contract, binding, contract.Name);
            }

            return host;
        }

        private Dictionary<string, ProviderBase<T>> m_ProviderDict = new Dictionary<string, ProviderBase<T>>(StringComparer.OrdinalIgnoreCase);

        public bool Setup(IServerConfig config)
        {
            return Setup(config, null);
        }

        public bool Setup(IServerConfig config, object protocol)
        {
            return Setup(config, protocol, string.Empty);
        }

        public bool Setup(IServerConfig config, object protocol, string consoleBaseAddress)
        {
            return Setup(config, protocol, consoleBaseAddress, string.Empty);
        }

        public bool Setup(IServerConfig config, object protocol, string consoleBaseAddress, string assembly)
        {
            Config = config;
            m_ConsoleBaseAddress = consoleBaseAddress;
            Protocol = protocol;

            if (!SetupLocalEndpoint(config))
            {
                LogUtil.LogError(this, "Invalid config ip/port");
                return false;
            }

            if (Protocol == null)
                Protocol = new CommandLineProtocol();

            var commandParserProtocol = Protocol as ICommandParserProtocol;

            if (CommandParser == null)
                CommandParser = commandParserProtocol.CommandParser;

            if (CommandParameterParser == null)
                CommandParameterParser = commandParserProtocol.CommandParameterParser;

            if (!LoadCommands())
                return false;

            if (!string.IsNullOrEmpty(assembly))
            {
                if (!SetupServiceProviders(config, assembly))
                    return false;
            }

            if (config.Certificate != null
                && config.Certificate.IsEnabled)
            {
                if (!SetupCertificate(config))
                    return false;
            }

            return SetupSocketServer();
        }

        private bool SetupSocketServer()
        {
            switch(Config.Mode)
            {
                case(SocketMode.Sync):
                    var syncProtocol = GetProperProtocol<ISyncProtocol>(Config.Mode);
                    if (syncProtocol == null)
                        return false;
                    m_SocketServer = new SyncSocketServer<T>(this, m_LocalEndPoint, syncProtocol);
                    return true;
                case(SocketMode.Async):
                    var asyncProtocol = GetProperProtocol<IAsyncProtocol>(Config.Mode);
                    if (asyncProtocol == null)
                        return false;
                    m_SocketServer = new AsyncSocketServer<T>(this, m_LocalEndPoint, asyncProtocol);
                    return true;
                case(SocketMode.Udp):
                    var udpProtocol = GetProperProtocol<IAsyncProtocol>(Config.Mode);
                    if (udpProtocol == null)
                        return false;
                    m_SocketServer = new UdpSocketServer<T>(this, m_LocalEndPoint, udpProtocol);
                    return true;
                default:
                    LogUtil.LogError(this, "Unkonwn SocketMode: " + Config.Mode);
                    return false;
            }
        }

        private TProtocol GetProperProtocol<TProtocol>(SocketMode mode)
        {
            if (Protocol is TProtocol)
                return (TProtocol)Protocol;

            LogUtil.LogError(this, "The protocol doesn't support SocketModel: " + mode);
            return default(TProtocol);
        }

        private bool SetupLocalEndpoint(IServerConfig config)
        {
            if (config.Port > 0)
            {
                try
                {
                    if (string.IsNullOrEmpty(config.Ip) || "Any".Equals(config.Ip, StringComparison.OrdinalIgnoreCase))
                        m_LocalEndPoint = new IPEndPoint(IPAddress.Any, config.Port);
                    else
                        m_LocalEndPoint = new IPEndPoint(IPAddress.Parse(config.Ip), config.Port);

                    return true;
                }
                catch (Exception)
                {
                    return false;
                }
            }

            return false;
        }

        private bool SetupServiceProviders(IServerConfig config, string assembly)
        {
            string dir = Path.GetDirectoryName(this.GetType().Assembly.Location);

            string assemblyFile = Path.Combine(dir, assembly + ".dll");

            try
            {
                Assembly ass = Assembly.LoadFrom(assemblyFile);

                foreach(var providerType in ass.GetImplementTypes<ProviderBase<T>>())
                {
                    ProviderBase<T> provider = ass.CreateInstance(providerType.ToString()) as ProviderBase<T>;

                    if (provider.Init(this, config))
                    {
                        m_ProviderDict[provider.Name] = provider;
                    }
                    else
                    {
                        LogUtil.LogError(this, "Failed to initalize provider " + providerType.ToString() + "!");
                        return false;
                    }
                }

                if (!IsReady)
                {
                    LogUtil.LogError(this, "Failed to load service provider from assembly:" + assemblyFile);
                    return false;
                }

                return true;
            }
            catch (Exception e)
            {
                LogUtil.LogError(this, e);
                return false;
            }
        }

        private bool SetupCertificate(IServerConfig config)
        {
            try
            {                
                Certificate = CertificateManager.Initialize(config.Certificate);
                return true;
            }
            catch (Exception e)
            {
                LogUtil.LogError(this, "Failed to initialize certificate!", e);
                return false;
            }
        }

        public string Name
        {
            get { return Config.Name; }
        }

        private ISocketServer m_SocketServer;

        public virtual bool Start()
        {
            if (this.IsRunning)
            {
                LogUtil.LogError(this, "This socket server is running already, you needn't start it.");
                return false;
            }

            if (!m_SocketServer.Start())
                return false;

            if(Config.ClearIdleSession)
                StartClearSessionTimer();

            if (!StartConsoleHost())
            {
                LogUtil.LogError(this, "Failed to start console service host for " + Name);
                Stop();
                return false;
            }

            return true;
        }

        private ServiceHost m_ConsoleHost;

        private bool StartConsoleHost()
        {
            var consoleInfo = ConsoleHostInfo;

            if (consoleInfo == null)
                return true;

            m_ConsoleHost = CreateConsoleHost(consoleInfo);

            try
            {
                m_ConsoleHost.Open();
                return true;
            }
            catch (Exception e)
            {
                LogUtil.LogError(this, e);
                m_ConsoleHost = null;
                return false;
            }
        }

        private void CloseConsoleHost()
        {
            if (m_ConsoleHost == null)
                return;

            try
            {
                m_ConsoleHost.Close();
            }
            catch (Exception e)
            {
                LogUtil.LogError(this, "Failed to close console service host for " + Name, e);
            }
            finally
            {
                m_ConsoleHost = null;
            }
        }

        public virtual void Stop()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        public bool IsRunning
        {
            get
            {
                if (m_SocketServer == null)
                    return false;

                return m_SocketServer.IsRunning;
            }
        }

        protected ProviderBase<T> GetProviderByName(string providerName)
        {
            ProviderBase<T> provider = null;

            if (m_ProviderDict.TryGetValue(providerName, out provider))
            {
                return provider;
            }
            else
            {
                return null;
            }
        }

        public virtual bool IsReady
        {
            get { return true; }
        }

        private Dictionary<string, T> m_SessionDict = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);

        private object m_SessionSyncRoot = new object();

        public T CreateAppSession(ISocketSession socketSession)
        {
            T appSession = new T();
            appSession.Initialize(this, socketSession);
            socketSession.Closed += new EventHandler<SocketSessionClosedEventArgs>(socketSession_Closed);

            lock (m_SessionSyncRoot)
            {
                m_SessionDict[appSession.IdentityKey] = appSession;
            }

            LogUtil.LogInfo(this, "SocketSession " + socketSession.IdentityKey + " was accepted!");
            return appSession;
        }

        public T GetAppSessionByIndentityKey(string identityKey)
        {
            T targetSession;

            lock (m_SessionSyncRoot)
            {
                m_SessionDict.TryGetValue(identityKey, out targetSession);
                return targetSession;
            }
        }

        void socketSession_Closed(object sender, SocketSessionClosedEventArgs e)
        {
            //the sender is a sessionID
            string identityKey = e.IdentityKey;

            if (string.IsNullOrEmpty(identityKey))
                return;

            try
            {
                lock (m_SessionSyncRoot)
                {
                    m_SessionDict.Remove(identityKey);
                }
                LogUtil.LogInfo(this, "SocketSession " + identityKey + " was closed!");
            }
            catch (Exception exc)
            {
                LogUtil.LogError(this, exc);
            }
        }

        public int SessionCount
        {
            get
            {
                return m_SessionDict.Count;
            }
        }

        private System.Threading.Timer m_ClearIdleSessionTimer = null;

        private void StartClearSessionTimer()
        {
            int interval  = Config.ClearIdleSessionInterval * 1000;//in milliseconds
            m_ClearIdleSessionTimer = new System.Threading.Timer(ClearIdleSession, new object(), interval, interval);
        }

        private void ClearIdleSession(object state)
        {
            if(Monitor.TryEnter(state))
            {
                try
                {
                    lock (m_SessionSyncRoot)
                    {
                        m_SessionDict.Values.Where(s =>
                            DateTime.Now.Subtract(s.SocketSession.LastActiveTime).TotalSeconds > Config.IdleSessionTimeOut)
                            .ToList().ForEach(s =>
                            {
                                s.Close();
                                LogUtil.LogInfo(this, string.Format("The socket session: {0} has been closed for timeout!", s.IdentityKey));
                            });
                    }
                }
                catch (Exception e)
                {
                    LogUtil.LogError(this, "Clear idle session error!", e);
                }
                finally
                {
                    Monitor.Exit(state);
                }
            }
        }

        #region ICommandSource<T> Members

        public ICommand<T> GetCommandByName(string commandName)
        {
            ICommand<T> command;

            if (m_CommandDict.TryGetValue(commandName, out command))
                return command;
            else
                return null;
        }

        #endregion

        #region IDisposable Members

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (IsRunning)
                {
                    m_SocketServer.Stop();
                }

                if (m_ClearIdleSessionTimer != null)
                {
                    m_ClearIdleSessionTimer.Change(Timeout.Infinite, Timeout.Infinite);
                    m_ClearIdleSessionTimer.Dispose();
                    m_ClearIdleSessionTimer = null;
                }

                m_SessionDict.Values.ToList().ForEach(s => s.Close());

                CloseConsoleHost();     
            }
        }

        #endregion
    }
}
