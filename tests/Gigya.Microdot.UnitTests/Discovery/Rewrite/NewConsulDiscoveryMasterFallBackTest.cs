using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using Gigya.Common.Contracts.Exceptions;
using Gigya.Microdot.Fakes;
using Gigya.Microdot.Interfaces.Configuration;
using Gigya.Microdot.Interfaces.SystemWrappers;
using Gigya.Microdot.ServiceDiscovery;
using Gigya.Microdot.ServiceDiscovery.Rewrite;
using Gigya.Microdot.SharedLogic.Rewrite;
using Gigya.Microdot.Testing.Shared;
using Gigya.Microdot.Testing.Shared.Utils;
using Ninject;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using Node = Gigya.Microdot.ServiceDiscovery.Rewrite.Node;

namespace Gigya.Microdot.UnitTests.Discovery.Rewrite
{
    [TestFixture]
    public class NewConsulDiscoveryMasterFallBackTest
    {
        private const string ServiceVersion = "1.2.30.1234";
        private string _serviceName;
        private const string MASTER_ENVIRONMENT = "prod";
        private const string ORIGINATING_ENVIRONMENT = "fake_env";
        private readonly TimeSpan _timeOut = TimeSpan.FromSeconds(5);
        private Dictionary<string, string> _configDic;
        private TestingKernel<ConsoleLog> _unitTestingKernel;
        private Dictionary<string, INodeMonitor> _consulNodeMonitors;
        private Dictionary<string, Func<INode[]>> _consulNodesResults;
        private IServiceListMonitor _serviceListMonitor;
        private ImmutableHashSet<string> _consulServiceList;
        private IEnvironmentVariableProvider _environmentVariableProvider;
        private ManualConfigurationEvents _configRefresh;
        private IDateTime _dateTimeMock;
        private int id;
        private const int Repeat = 1;

        [SetUp]
        public void SetUp()
        {
            _unitTestingKernel?.Dispose();
            _serviceName = $"ServiceName{++id}";

            _environmentVariableProvider = Substitute.For<IEnvironmentVariableProvider>();
            _environmentVariableProvider.DataCenter.Returns("il3");
            _environmentVariableProvider.DeploymentEnvironment.Returns(ORIGINATING_ENVIRONMENT);
            _environmentVariableProvider.ConsulAddress.Returns((string)null);

            _configDic = new Dictionary<string, string> {{"Discovery.EnvironmentFallbackEnabled", "true"}};
            _unitTestingKernel = new TestingKernel<ConsoleLog>(k =>
            {
                k.Rebind<IEnvironmentVariableProvider>().ToConstant(_environmentVariableProvider);

                k.Rebind<INodeSourceLoader>().To<NodeSourceLoader>().InSingletonScope();
                SetupConsulMocks(k);

                _dateTimeMock = Substitute.For<IDateTime>();
                _dateTimeMock.Delay(Arg.Any<TimeSpan>()).Returns(c => Task.Delay(TimeSpan.FromMilliseconds(100)));
                k.Rebind<IDateTime>().ToConstant(_dateTimeMock);
            }, _configDic);
            _configRefresh = _unitTestingKernel.Get<ManualConfigurationEvents>();

            var environmentVariableProvider = _unitTestingKernel.Get<IEnvironmentVariableProvider>();
            Assert.AreEqual(_environmentVariableProvider, environmentVariableProvider);
        }

        private void SetupConsulMocks(IKernel kernel)
        {
            _consulNodeMonitors = new Dictionary<string, INodeMonitor>();
            _consulNodesResults = new Dictionary<string, Func<INode[]>>();

            _serviceListMonitor = Substitute.For<IServiceListMonitor>();
            _consulServiceList = new HashSet<string>().ToImmutableHashSet();
            _serviceListMonitor.Services.Returns(_ => _consulServiceList);

            kernel.Rebind<Func<string, INodeMonitor>>().ToMethod(_ => (s => _consulNodeMonitors[s]));
            kernel.Rebind<IServiceListMonitor>().ToMethod(_ => _serviceListMonitor);

            CreateConsulMock(MasterService);
            CreateConsulMock(OriginatingService);

        }

        private void CreateConsulMock(string serviceName)
        {
            var mock = Substitute.For<INodeMonitor>();
            _consulNodesResults[serviceName] = () => new INode[] {new Node(hostName: "dummy", version: ServiceVersion)};
            mock.Nodes.Returns(_=>_consulNodesResults[serviceName]());
            _consulNodeMonitors[serviceName] = mock;

            _consulServiceList = _consulServiceList.Add(serviceName);
        }

        [TearDown]
        public void TearDown()
        {
            _unitTestingKernel.Dispose();
            foreach (var consulClient in _consulNodeMonitors)
            {
                consulClient.Value.Dispose();
            }
        }

        [Test]
        [Repeat(Repeat)]
        public async Task QueryNotDefinedShouldFallBackToMaster()
        {
            SetMockToReturnHost(MasterService);
            SetMockToReturnServiceNotDefined(OriginatingService);
            var nextHost = GetServiceDiscovey().GetNextHost();
            nextHost.Result.HostName.ShouldBe(MasterService);
        }

        [Test]
        [Ignore("New ServiceDiscovery implementation does not support DataCenter scopes")]
        [Repeat(Repeat)]
        public async Task ScopeDataCenterShouldUseServiceNameAsConsoleQuery()
        {
            _configDic[$"Discovery.Services.{_serviceName}.Scope"] = "DataCenter";
            SetMockToReturnHost(_serviceName);
            var nextHost = GetServiceDiscovey().GetNextHost();
            (await nextHost).HostName.ShouldBe(_serviceName);
        }

        [Test]
        [Repeat(Repeat)]
        public async Task WhenQueryDeleteShouldFallBackToMaster()
        {
            var reloadInterval = TimeSpan.FromMilliseconds(5);
            _configDic[$"Discovery.Services.{_serviceName}.ReloadInterval"] = reloadInterval.ToString();

            SetMockToReturnHost(MasterService);
            SetMockToReturnHost(OriginatingService);

            var discovey = GetServiceDiscovey();
            var waitForEvents = discovey.EndPointsChanged.WhenEventReceived(_timeOut);

            var nextHost = discovey.GetNextHost();
            (await nextHost).HostName.ShouldBe(OriginatingService);

            SetMockToReturnServiceNotDefined(OriginatingService);
            await waitForEvents;

            nextHost = discovey.GetNextHost();
            (await nextHost).HostName.ShouldBe(MasterService);
        }

        [Test]
        [Repeat(Repeat)]
        public async Task WhenQueryAddShouldNotFallBackToMaster()
        {
            var reloadInterval = TimeSpan.FromMilliseconds(5);
            _configDic[$"Discovery.Services.{_serviceName}.ReloadInterval"] = reloadInterval.ToString();

            SetMockToReturnHost(MasterService);
            SetMockToReturnServiceNotDefined(OriginatingService);

            var discovey = GetServiceDiscovey();

            var nextHost = discovey.GetNextHost();
            (await nextHost).HostName.ShouldBe(MasterService);

            var waitForEvents = discovey.EndPointsChanged.WhenEventReceived(_timeOut);
            SetMockToReturnHost(OriginatingService);
            await waitForEvents;

            nextHost = discovey.GetNextHost();
            nextHost.Result.HostName.ShouldBe(OriginatingService);
        }

        [Test]
        [Repeat(Repeat)]
        public async Task ShouldNotFallBackToMasterOnConsulError()
        {
            SetMockToReturnHost(MasterService);
            SetMockToReturnError(OriginatingService);
            Should.Throw<EnvironmentException>(() => GetServiceDiscovey().GetNextHost());
        }

        [Test]
        [Repeat(Repeat)]
        public async Task QueryDefinedShouldNotFallBackToMaster()
        {
            SetMockToReturnHost(MasterService);
            SetMockToReturnHost(OriginatingService);

            var nextHost = GetServiceDiscovey().GetNextHost();
            (await nextHost).HostName.ShouldBe(OriginatingService);
        }

        [Test]
        [Repeat(Repeat)]
        public void MasterShouldNotFallBack()
        {
            _environmentVariableProvider = Substitute.For<IEnvironmentVariableProvider>();
            _environmentVariableProvider.DataCenter.Returns("il3");
            _environmentVariableProvider.DeploymentEnvironment.Returns(MASTER_ENVIRONMENT);
            _unitTestingKernel.Rebind<IEnvironmentVariableProvider>().ToConstant(_environmentVariableProvider);

            SetMockToReturnServiceNotDefined(MasterService);

            Should.Throw<EnvironmentException>(() => GetServiceDiscovey().GetNextHost());
        }

        [Test]
        [Repeat(Repeat)]
        public async Task EndPointsChangedShouldNotFireWhenNothingChange()
        {
            TimeSpan reloadInterval = TimeSpan.FromMilliseconds(5);
            _configDic[$"Discovery.Services.{_serviceName}.ReloadInterval"] = reloadInterval.ToString();
            int numOfEvent = 0;
            SetMockToReturnHost(MasterService);
            SetMockToReturnHost(OriginatingService);

            //in the first time can fire one or two event
            var discovey = GetServiceDiscovey();
            discovey.GetNextHost();
            discovey.EndPointsChanged.LinkTo(new ActionBlock<string>(x => numOfEvent++));
            Thread.Sleep(200);
            numOfEvent = 0;

            for (int i = 0; i < 5; i++)
            {
                discovey.GetNextHost();
                Thread.Sleep((int) reloadInterval.TotalMilliseconds * 10);
            }
            numOfEvent.ShouldBe(0);
        }

        [Test]
        [Repeat(Repeat)]
        public async Task EndPointsChangedShouldFireConfigChange()
        {
            SetMockToReturnHost(MasterService);
            SetMockToReturnHost(OriginatingService);

            //in the first time can fire one or two event
            var discovey = GetServiceDiscovey();
            var waitForEvents = discovey.EndPointsChanged.StartCountingEvents();

            await discovey.GetNextHost();

            _configDic[$"Discovery.Services.{_serviceName}.Hosts"] = "localhost";
            _configDic[$"Discovery.Services.{_serviceName}.Source"] = "Config";

            Task waitForChangeEvent = waitForEvents.WhenNextEventReceived();
            _configRefresh.RaiseChangeEvent();
            await waitForChangeEvent;
            var host = await discovey.GetNextHost();
            host.HostName.ShouldBe("localhost");
            waitForEvents.ReceivedEvents.Count.ShouldBe(1);

        }

        [Test]
        [Repeat(Repeat)]
        public async Task GetAllEndPointsChangedShouldFireConfigChange()
        {
            SetMockToReturnHost(MasterService);
            SetMockToReturnHost(OriginatingService);

            //in the first time can fire one or two event
            var discovey = GetServiceDiscovey();

            //wait for discovey to be initialize!!
            var endPoints = await discovey.GetAllEndPoints();
            endPoints.Single().HostName.ShouldBe(OriginatingService);

            var waitForEvents = discovey.EndPointsChanged.StartCountingEvents();

            _configDic[$"Discovery.Services.{_serviceName}.Source"] = "Config";
            _configDic[$"Discovery.Services.{_serviceName}.Hosts"] = "localhost";
            Console.WriteLine("RaiseChangeEvent");

            Task waitForChangeEvent = waitForEvents.WhenNextEventReceived();
            _configRefresh.RaiseChangeEvent();
            await waitForChangeEvent;
            waitForEvents.ReceivedEvents.Count.ShouldBe(1);


            endPoints = await discovey.GetAllEndPoints();
            endPoints.Single().HostName.ShouldBe("localhost");
            waitForEvents.ReceivedEvents.Count.ShouldBe(1);
        }

        [Test]
        [Repeat(Repeat)]
        public async Task EndPointsChangedShouldFireWhenHostChange()
        {
            var reloadInterval = TimeSpan.FromMilliseconds(5);
            _configDic[$"Discovery.Services.{_serviceName}.ReloadInterval"] = reloadInterval.ToString();
            SetMockToReturnHost(MasterService);
            SetMockToReturnHost(OriginatingService);
            var discovey = GetServiceDiscovey();
            await discovey.GetAllEndPoints();

            var wait = discovey.EndPointsChanged.StartCountingEvents();
            bool UseOriginatingService(int i) => i % 2 == 0;
            for (int i = 1; i < 6; i++)
            {
                var waitForNextEvent = wait.WhenNextEventReceived();
                //act                
                if (UseOriginatingService(i))
                    SetMockToReturnHost(OriginatingService);
                else
                    SetMockToReturnServiceNotDefined(OriginatingService);

                await waitForNextEvent;
                //assert
                wait.ReceivedEvents.Count.ShouldBe(i);
                var nextHost = (await discovey.GetNextHost()).HostName;
                if (UseOriginatingService(i))
                    nextHost.ShouldBe(OriginatingService);
                else
                    nextHost.ShouldBe(MasterService);
            }
        }

        private void SetMockToReturnHost(string serviceName)
        {
            if (!_consulNodeMonitors.ContainsKey(serviceName))
                CreateConsulMock(serviceName);

            var newNodes = new INode[]{new Node(serviceName)};
            _consulNodesResults[serviceName] = () => newNodes;

            _consulServiceList = _consulServiceList.Add(serviceName);
        }

        private void SetMockToReturnServiceNotDefined(string serviceName)
        {
            _consulServiceList = _consulServiceList.Remove(serviceName);
        }

        private void SetMockToReturnError(string serviceName)
        {
            _consulNodesResults[serviceName] = () => throw new EnvironmentException("Mock: some error");
        }

        [Test]
        public void ServiceDiscoveySameNameShouldBeTheSame()
        {
            Assert.AreEqual(GetServiceDiscovey(), GetServiceDiscovey());
        }

        private readonly ReachabilityChecker _reachabilityChecker = x => Task.FromResult(true);

        private IServiceDiscovery GetServiceDiscovey()
        {
            var discovery =
                _unitTestingKernel.Get<Func<string, ReachabilityChecker, NewServiceDiscovery>>()(_serviceName,
                    _reachabilityChecker);
            return discovery;
        }


        private string MasterService => ConsulServiceName(_serviceName, MASTER_ENVIRONMENT);
        private string OriginatingService => ConsulServiceName(_serviceName, ORIGINATING_ENVIRONMENT);

        private static string ConsulServiceName(string serviceName, string deploymentEnvironment) =>
            $"{serviceName}-{deploymentEnvironment}";
    }
}