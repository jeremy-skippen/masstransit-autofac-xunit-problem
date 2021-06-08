using System;
using System.Linq;
using System.Threading.Tasks;

using Autofac;
using Autofac.Extensions.DependencyInjection;

using Automatonymous;

using MassTransit;
using MassTransit.AutofacIntegration;
using MassTransit.Saga;
using MassTransit.Testing;

using Microsoft.Extensions.DependencyInjection;

using Xunit;

namespace Example
{
    public interface IExampleDependency
    {
        Task DoSomething();
    }

    public class ExampleDependency : IExampleDependency
    {
        public async Task DoSomething()
        {
            await Task.CompletedTask;
        }
    }

    public class ExampleMessage
    {
        public Guid CorrelationId { get; init; }

        public string Message { get; init; }
    }

    public class ExampleMessage2
    {
        public Guid CorrelationId { get; init; }

        public string Message { get; init; }
    }

    public class ExampleEvent
    {
        public Guid CorrelationId { get; init; }

        public string Message { get; init; }
    }

    public class ExampleEvent2
    {
        public Guid CorrelationId { get; init; }

        public string Message { get; init; }
    }

    public class ExampleEvent3
    {
        public Guid CorrelationId { get; init; }

        public string Message { get; init; }
    }

    public class ExampleConsumer : IConsumer<ExampleMessage>
    {
        private readonly IExampleDependency _dependency;

        public ExampleConsumer(IExampleDependency dependency)
        {
            _dependency = dependency;
        }

        public async Task Consume(ConsumeContext<ExampleMessage> context)
        {
            await _dependency.DoSomething();

            await context.Publish(new ExampleEvent2
            {
                CorrelationId = context.Message.CorrelationId,
                Message = "Progress state machine",
            });
        }
    }

    public class ExampleConsumer2 : IConsumer<ExampleMessage2>
    {
        private readonly IExampleDependency _dependency;

        public ExampleConsumer2(IExampleDependency dependency)
        {
            _dependency = dependency;
        }

        public async Task Consume(ConsumeContext<ExampleMessage2> context)
        {
            await _dependency.DoSomething();

            await context.Publish(new ExampleEvent3
            {
                CorrelationId = context.Message.CorrelationId,
                Message = "Finalize state machine",
            });
        }
    }

    public class ExampleState : SagaStateMachineInstance
    {
        public Guid CorrelationId { get; set; }

        public int CurrentState { get; set; }

        public string Message { get; set; }
    }

    public class ExampleStateMachine : MassTransitStateMachine<ExampleState>
    {
        private readonly IExampleDependency _dependency;

        public Event<ExampleEvent> ExampleEvent1 { get; }
        public Event<ExampleEvent2> ExampleEvent2 { get; }
        public Event<ExampleEvent3> ExampleEvent3 { get; }

        public State ExampleState { get; set; }
        public State ExampleState2 { get; set; }

        public ExampleStateMachine(IExampleDependency dependency)
        {
            _dependency = dependency;

            Event(
                () => ExampleEvent1,
                cfg =>
                {
                    cfg.CorrelateBy((e, ctx) => e.CorrelationId == ctx.Message.CorrelationId);
                    cfg.InsertOnInitial = true;
                }
            );
            Event(
                () => ExampleEvent2,
                cfg => cfg.CorrelateBy((e, ctx) => e.CorrelationId == ctx.Message.CorrelationId)
            );

            InstanceState(x => x.CurrentState, ExampleState, ExampleState2);

            Initially(
                When(ExampleEvent1)
                    .Then(ctx =>
                    {
                        ctx.Instance.Message = ctx.Data.Message;
                    })
                    .TransitionTo(ExampleState)
                    .PublishAsync(ctx => ctx.Init<ExampleMessage>(new ExampleMessage
                    {
                        CorrelationId = ctx.Instance.CorrelationId,
                        Message = "Trigger consumer",
                    }))
            );

            During(
                ExampleState,
                When(ExampleEvent2)
                    .Then(ctx =>
                    {
                        ctx.Instance.Message = ctx.Data.Message;
                    })
                    .ThenAsync(async ctx => await _dependency.DoSomething())
                    .Finalize()
            );

            During(
                ExampleState2,
                When(ExampleEvent3)
                    .Then(ctx =>
                    {
                        ctx.Instance.Message = ctx.Data.Message;
                    })
                    .ThenAsync(async ctx => await _dependency.DoSomething())
                    .Finalize()
            );
        }
    }

    public class ExampleTestClass
    {
        /// <summary>
        /// This test mostly works as expected, however the test harnesses for the SagaStateMachine and Consumer
        /// don't capture any messages, even though the saga and consumer execute as expected.
        /// </summary>
        [Fact]
        public async Task ExampleTest_AutofacOnly()
        {
            var builder = new ContainerBuilder();

            builder.RegisterType<ExampleDependency>().As<IExampleDependency>();
            builder.AddMassTransitInMemoryTestHarness(cfg =>
            {
                cfg.AddSagaStateMachine<ExampleStateMachine, ExampleState>()
                    .InMemoryRepository();

                cfg.AddConsumer<ExampleConsumer>();
                cfg.AddConsumer<ExampleConsumer2>();
            });

            var container = builder.Build();
            var testHarness = container.Resolve<InMemoryTestHarness>();

            try
            {
                await testHarness.Start();

                // This doesn't work using autofac
                // var sagaHarness = container.Resolve<IStateMachineSagaTestHarness<ExampleState, ExampleStateMachine>>();
                // var consumerHarness = container.Resolve<IConsumerTestHarness<ExampleConsumer>>();

                var saga = container.Resolve<ExampleStateMachine>();
                var sagaRepo = container.Resolve<ISagaRepository<ExampleState>>();
                var sagaHarness = testHarness.StateMachineSaga(saga, sagaRepo);
                var consumerHarness = testHarness.Consumer(() => container.Resolve<ExampleConsumer>());
                var consumer2Harness = testHarness.Consumer(() => container.Resolve<ExampleConsumer2>());

                var correlationId = Guid.NewGuid();

                await testHarness.Bus.Publish(new ExampleEvent
                {
                    CorrelationId = correlationId,
                    Message = "Trigger state machine",
                });

                // 1. Check that ExampleEvent has been published recieved
                Assert.True(await testHarness.Published.Any<ExampleEvent>(m => m.Context.CorrelationId == correlationId));
                Assert.True(await testHarness.Consumed.Any<ExampleEvent>(m => m.Context.CorrelationId == correlationId));
                // FAILS: Assert.True(await sagaHarness.Consumed.Any<ExampleEvent>(m => m.Context.CorrelationId == correlationId));
                // FAILS: Assert.True(await sagaHarness.Created.Any(t => t.CorrelationId == correlationId));

                // 2. Check that ExampleMessage has been published and received
                Assert.True(await testHarness.Published.Any<ExampleMessage>(m => m.Context.CorrelationId == correlationId));
                Assert.True(await testHarness.Consumed.Any<ExampleMessage>(m => m.Context.CorrelationId == correlationId));
                // FAILS: Assert.True(await consumerHarness.Consumed.Any<ExampleMessage>(m => m.Context.CorrelationId == correlationId));

                // 3. Check that ExampleEvent2 has been published and recieved
                Assert.True(await testHarness.Published.Any<ExampleEvent2>(m => m.Context.CorrelationId == correlationId));
                Assert.True(await testHarness.Consumed.Any<ExampleEvent2>(m => m.Context.CorrelationId == correlationId));
                // FAILS: Assert.True(await sagaHarness.Consumed.Any<ExampleEvent2>(m => m.Context.CorrelationId == correlationId));

                // In my actual unit test the code below causes the test to fail
                var state = sagaHarness.Sagas.Select(s => s.CorrelationId == correlationId).Single();
                Assert.Equal("Progress state machine", state.Saga.Message);

                // 4. Trigger the second consumer
                await testHarness.Bus.Publish(new ExampleMessage2
                {
                    CorrelationId = correlationId,
                    Message = "Trigger consumer",
                });

                // 5. Check that ExampleMessage has been published and received
                Assert.True(await testHarness.Published.Any<ExampleMessage2>(m => m.Context.CorrelationId == correlationId));
                Assert.True(await testHarness.Consumed.Any<ExampleMessage2>(m => m.Context.CorrelationId == correlationId));
                // FAILS: Assert.True(await consumer2Harness.Consumed.Any<ExampleMessage2>(m => m.Context.CorrelationId == correlationId));

                // 6. Check that ExampleEvent3 has been published and recieved
                Assert.True(await testHarness.Published.Any<ExampleEvent2>(m => m.Context.CorrelationId == correlationId));
                Assert.True(await testHarness.Consumed.Any<ExampleEvent2>(m => m.Context.CorrelationId == correlationId));
                // FAILS: Assert.True(await sagaHarness.Consumed.Any<ExampleEvent2>(m => m.Context.CorrelationId == correlationId));
            }
            finally
            {
                await testHarness.Stop();
            }
        }

        /// <summary>
        /// This test works as expected.
        /// </summary>
        [Fact]
        public async Task ExampleTest_AutofacAndServiceProvider()
        {
            var builder = new ContainerBuilder();

            builder.RegisterType<ExampleDependency>().As<IExampleDependency>();

            var collection = new ServiceCollection()
                .AddMassTransitInMemoryTestHarness(cfg =>
                {
                    cfg.AddSagaStateMachine<ExampleStateMachine, ExampleState>()
                        .InMemoryRepository();
                    cfg.AddSagaStateMachineTestHarness<ExampleStateMachine, ExampleState>();

                    cfg.AddConsumer<ExampleConsumer>();
                    cfg.AddConsumerTestHarness<ExampleConsumer>();
                    cfg.AddConsumer<ExampleConsumer2>();
                    cfg.AddConsumerTestHarness<ExampleConsumer2>();
                });
            builder.Populate(collection);

            var container = builder.Build();
            var provider = new AutofacServiceProvider(container);

            var testHarness = provider.GetRequiredService<InMemoryTestHarness>();

            try
            {
                await testHarness.Start();

                var sagaHarness = provider.GetRequiredService<IStateMachineSagaTestHarness<ExampleState, ExampleStateMachine>>();
                var consumerHarness = provider.GetRequiredService<IConsumerTestHarness<ExampleConsumer>>();
                var consumer2Harness = provider.GetRequiredService<IConsumerTestHarness<ExampleConsumer2>>();

                var correlationId = Guid.NewGuid();

                await testHarness.Bus.Publish(new ExampleEvent
                {
                    CorrelationId = correlationId,
                    Message = "Trigger state machine",
                });

                // 1. Check that ExampleEvent has been published recieved
                Assert.True(await testHarness.Published.Any<ExampleEvent>(m => m.Context.CorrelationId == correlationId));
                Assert.True(await testHarness.Consumed.Any<ExampleEvent>(m => m.Context.CorrelationId == correlationId));
                Assert.True(await sagaHarness.Consumed.Any<ExampleEvent>(m => m.Context.CorrelationId == correlationId));
                Assert.True(await sagaHarness.Created.Any(t => t.CorrelationId == correlationId));

                // 2. Check that ExampleMessage has been published and received
                Assert.True(await testHarness.Published.Any<ExampleMessage>(m => m.Context.CorrelationId == correlationId));
                Assert.True(await testHarness.Consumed.Any<ExampleMessage>(m => m.Context.CorrelationId == correlationId));
                Assert.True(await consumerHarness.Consumed.Any<ExampleMessage>(m => m.Context.CorrelationId == correlationId));

                // 3. Check that ExampleEvent2 has been published and recieved
                Assert.True(await testHarness.Published.Any<ExampleEvent2>(m => m.Context.CorrelationId == correlationId));
                Assert.True(await testHarness.Consumed.Any<ExampleEvent2>(m => m.Context.CorrelationId == correlationId));
                Assert.True(await sagaHarness.Consumed.Any<ExampleEvent2>(m => m.Context.CorrelationId == correlationId));

                // In my actual unit test the code below causes the test to fail
                var state = sagaHarness.Sagas.Select(s => s.CorrelationId == correlationId).Single();
                Assert.Equal("Progress state machine", state.Saga.Message);

                // 4. Trigger the second consumer
                await testHarness.Bus.Publish(new ExampleMessage2
                {
                    CorrelationId = correlationId,
                    Message = "Trigger consumer",
                });

                // 5. Check that ExampleMessage has been published and received
                Assert.True(await testHarness.Published.Any<ExampleMessage2>(m => m.Context.CorrelationId == correlationId));
                Assert.True(await testHarness.Consumed.Any<ExampleMessage2>(m => m.Context.CorrelationId == correlationId));
                Assert.True(await consumer2Harness.Consumed.Any<ExampleMessage2>(m => m.Context.CorrelationId == correlationId));

                // 6. Check that ExampleEvent3 has been published and recieved
                Assert.True(await testHarness.Published.Any<ExampleEvent2>(m => m.Context.CorrelationId == correlationId));
                Assert.True(await testHarness.Consumed.Any<ExampleEvent2>(m => m.Context.CorrelationId == correlationId));
                Assert.True(await sagaHarness.Consumed.Any<ExampleEvent2>(m => m.Context.CorrelationId == correlationId));
            }
            finally
            {
                await testHarness.Stop();
            }
        }
    }
}
