﻿extern alias ReferencedDapper;

using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using System.Threading;
using ReferencedDapper::Dapper;
using Hangfire.Common;
using Hangfire.Server;
using Hangfire.Storage;
using Moq;
using Xunit;
// ReSharper disable PossibleNullReferenceException

// ReSharper disable AssignNullToNotNullAttribute

namespace Hangfire.SqlServer.Tests
{
    public class SqlServerConnectionFacts
    {
        private readonly Mock<IPersistentJobQueue> _queue;
        private readonly PersistentJobQueueProviderCollection _providers;

        public SqlServerConnectionFacts()
        {
            _queue = new Mock<IPersistentJobQueue>();

            var provider = new Mock<IPersistentJobQueueProvider>();
            provider.Setup(x => x.GetJobQueue())
                .Returns(_queue.Object);

            _providers = new PersistentJobQueueProviderCollection(provider.Object);
        }

        [Fact]
        public void Ctor_ThrowsAnException_WhenStorageIsNull()
        {
            var exception = Assert.Throws<ArgumentNullException>(
                () => new SqlServerConnection(null));

            Assert.Equal("storage", exception.ParamName);
        }

        [Fact, CleanDatabase]
        public void FetchNextJob_DelegatesItsExecution_ToTheQueue()
        {
            UseConnection(connection =>
            {
                var token = new CancellationToken();
                var queues = new[] { "default" };

                connection.FetchNextJob(queues, token);

                _queue.Verify(x => x.Dequeue(queues, token));
            });
        }

        [Fact, CleanDatabase]
        public void FetchNextJob_Throws_IfMultipleProvidersResolved()
        {
            UseConnection(connection =>
            {
                var token = new CancellationToken();
                var anotherProvider = new Mock<IPersistentJobQueueProvider>();
                _providers.Add(anotherProvider.Object, new [] { "critical" });

                Assert.Throws<InvalidOperationException>(
                    () => connection.FetchNextJob(new[] { "critical", "default" }, token));
            });
        }

        [Fact, CleanDatabase]
        public void CreateWriteTransaction_ReturnsNonNullInstance()
        {
            UseConnection(connection =>
            {
                var transaction = connection.CreateWriteTransaction();
                Assert.NotNull(transaction);
            });
        }

        [Fact, CleanDatabase]
        public void AcquireLock_ReturnsNonNullInstance()
        {
            UseConnection(connection =>
            {
                var @lock = connection.AcquireDistributedLock("1", TimeSpan.FromSeconds(1));
                Assert.NotNull(@lock);
            });
        }

        [Fact, CleanDatabase]
        public void AcquireDistributedLock_ThrowsAnException_WhenResourceIsNullOrEmpty()
        {
            UseConnection(connection =>
            {
                var exception = Assert.Throws<ArgumentNullException>(
                () => connection.AcquireDistributedLock("", TimeSpan.FromMinutes(5)));

                Assert.Equal("resource", exception.ParamName);
            });
        }

        [Fact, CleanDatabase]
        public void AcquireDistributedLock_AcquiresExclusiveApplicationLock_OnSession()
        {
            UseConnections((sql, connection) =>
            {
                using (connection.AcquireDistributedLock("hello", TimeSpan.FromMinutes(5)))
                {
                    var lockMode = sql.Query<string>(
                        $"select applock_mode('public', '{Constants.DefaultSchema}:hello', 'session')").Single();

                    Assert.Equal("Exclusive", lockMode);
                }
            });
        }

        [Fact, CleanDatabase]
        public void AcquireDistributedLock_ThrowsAnException_IfLockCanNotBeGranted()
        {
            var releaseLock = new ManualResetEventSlim(false);
            var lockAcquired = new ManualResetEventSlim(false);

            var thread = new Thread(
                () => UseConnection(connection1 =>
                {
                    using (connection1.AcquireDistributedLock("exclusive", TimeSpan.FromSeconds(5)))
                    {
                        lockAcquired.Set();
                        releaseLock.Wait();
                    }
                }));
            thread.Start();

            lockAcquired.Wait();

            UseConnection(connection2 =>
            {
                Assert.Throws<DistributedLockTimeoutException>(
                    () =>
                    {
                        using (connection2.AcquireDistributedLock("exclusive", TimeSpan.FromSeconds(5)))
                        {
                        }
                    });
            });

            releaseLock.Set();
            thread.Join();
        }

        [Fact, CleanDatabase]
        public void AcquireDistributedLock_Dispose_ReleasesExclusiveApplicationLock()
        {
            UseConnections((sql, connection) =>
            {
                var distributedLock = connection.AcquireDistributedLock("hello", TimeSpan.FromMinutes(5));
                distributedLock.Dispose();

                var lockMode = sql.Query<string>(
                    "select applock_mode('public', 'hello', 'session')").Single();

                Assert.Equal("NoLock", lockMode);
            });
        }

        [Fact, CleanDatabase]
        public void AcquireDistributedLock_IsReentrant_FromTheSameConnection_OnTheSameResource()
        {
            UseConnection(connection =>
            {
                using (connection.AcquireDistributedLock("hello", TimeSpan.FromMinutes(5)))
                using (connection.AcquireDistributedLock("hello", TimeSpan.FromMinutes(5)))
                {
                    Assert.True(true);
                }
            });
        }

        [Fact, CleanDatabase]
        public void CreateExpiredJob_ThrowsAnException_WhenJobIsNull()
        {
            UseConnection(connection =>
            {
                var exception = Assert.Throws<ArgumentNullException>(
                    () => connection.CreateExpiredJob(
                        null,
                        new Dictionary<string, string>(),
                        DateTime.UtcNow,
                        TimeSpan.Zero));

                Assert.Equal("job", exception.ParamName);
            });
        }

        [Fact, CleanDatabase]
        public void CreateExpiredJob_ThrowsAnException_WhenParametersCollectionIsNull()
        {
            UseConnection(connection =>
            {
                var exception = Assert.Throws<ArgumentNullException>(
                    () => connection.CreateExpiredJob(
                        Job.FromExpression(() => SampleMethod("hello")),
                        null,
                        DateTime.UtcNow,
                        TimeSpan.Zero));

                Assert.Equal("parameters", exception.ParamName);
            });
        }

        [Theory, CleanDatabase]
        [InlineData(true)]
        [InlineData(false)]
        public void CreateExpiredJob_CreatesAJobInTheStorage_AndSetsItsParameters(bool useBatching)
        {
            UseConnections((sql, connection) =>
            {
                var createdAt = new DateTime(2012, 12, 12);
                var jobId = connection.CreateExpiredJob(
                    Job.FromExpression(() => SampleMethod("Hello")),
                    new Dictionary<string, string> { { "Key1", "Value1" }, { "Key2", "Value2" }, { "Key3", "Value3" } },
                    createdAt,
                    TimeSpan.FromDays(1));

                Assert.NotNull(jobId);
                Assert.NotEmpty(jobId);

                var sqlJob = sql.Query($"select * from [{Constants.DefaultSchema}].Job").Single();
                Assert.Equal(jobId, sqlJob.Id.ToString());
                Assert.Equal(createdAt, sqlJob.CreatedAt);
                Assert.Equal(null, (int?) sqlJob.StateId);
                Assert.Equal(null, (string) sqlJob.StateName);

                var invocationData = InvocationData.DeserializePayload((string)sqlJob.InvocationData);
                invocationData.Arguments = sqlJob.Arguments;

                var job = invocationData.Deserialize();
                Assert.Equal(typeof(SqlServerConnectionFacts), job.Type);
                Assert.Equal("SampleMethod", job.Method.Name);
                Assert.Equal("Hello", job.Args[0]);

                Assert.True(createdAt.AddDays(1).AddMinutes(-1) < sqlJob.ExpireAt);
                Assert.True(sqlJob.ExpireAt < createdAt.AddDays(1).AddMinutes(1));

                var parameters = sql.Query(
                    $"select * from [{Constants.DefaultSchema}].JobParameter where JobId = @id",
                    new { id = jobId })
                    .ToDictionary(x => (string) x.Name, x => (string) x.Value);

                Assert.Equal("Value1", parameters["Key1"]);
                Assert.Equal("Value2", parameters["Key2"]);
                Assert.Equal("Value3", parameters["Key3"]);
            }, useBatching);
        }

        [Theory, CleanDatabase]
        [InlineData(true)]
        [InlineData(false)]
        public void CreateExpiredJob_CanCreateParametersWithNullValues(bool useBatching)
        {
            UseConnections((sql, connection) =>
            {
                var createdAt = new DateTime(2012, 12, 12);
                var jobId = connection.CreateExpiredJob(
                    Job.FromExpression(() => SampleMethod("Hello")),
                    new Dictionary<string, string> { { "Key1", null } },
                    createdAt,
                    TimeSpan.FromDays(1));

                var parameters = sql
                    .Query($"select * from [{Constants.DefaultSchema}].JobParameter where JobId = @id", new { id = jobId })
                    .ToDictionary(x => (string)x.Name, x => (string)x.Value);

                Assert.Equal(null, parameters["Key1"]);
            }, useBatching);
        }

        [Theory, CleanDatabase]
        [InlineData(true)]
        [InlineData(false)]
        public void CreateExpiredJob_CanCreateTwoParametersWithNullValues(bool useBatching)
        {
            UseConnections((sql, connection) =>
            {
                var createdAt = new DateTime(2012, 12, 12);
                var jobId = connection.CreateExpiredJob(
                    Job.FromExpression(() => SampleMethod("Hello")),
                    new Dictionary<string, string> { { "Key1", null }, { "Key2", null } },
                    createdAt,
                    TimeSpan.FromDays(1));

                var parameters = sql
                    .Query($"select * from [{Constants.DefaultSchema}].JobParameter where JobId = @id", new { id = jobId })
                    .ToDictionary(x => (string)x.Name, x => (string)x.Value);

                Assert.Equal(null, parameters["Key1"]);
                Assert.Equal(null, parameters["Key2"]);
            }, useBatching);
        }

        [Theory, CleanDatabase]
        [InlineData(true)]
        [InlineData(false)]
        public void CreateExpiredJob_CanCreateMultipleParametersWithNullValues(bool useBatching)
        {
            UseConnections((sql, connection) =>
            {
                var createdAt = new DateTime(2012, 12, 12);
                var jobId = connection.CreateExpiredJob(
                    Job.FromExpression(() => SampleMethod("Hello")),
                    new Dictionary<string, string> { { "Key1", null }, { "Key2", null }, { "Key3", null } },
                    createdAt,
                    TimeSpan.FromDays(1));

                var parameters = sql
                    .Query($"select * from [{Constants.DefaultSchema}].JobParameter where JobId = @id", new { id = jobId })
                    .ToDictionary(x => (string)x.Name, x => (string)x.Value);

                Assert.Equal(null, parameters["Key1"]);
                Assert.Equal(null, parameters["Key2"]);
                Assert.Equal(null, parameters["Key3"]);
            }, useBatching);
        }

        [Theory, CleanDatabase]
        [InlineData(true)]
        [InlineData(false)]
        public void CreateExpiredJob_CanCreateJobWithoutParameters(bool useBatching)
        {
            UseConnections((sql, connection) =>
            {
                var createdAt = new DateTime(2012, 12, 12);
                var jobId = connection.CreateExpiredJob(
                    Job.FromExpression(() => SampleMethod("Hello")),
                    new Dictionary<string, string>(), 
                    createdAt,
                    TimeSpan.FromDays(1));

                var parameters = sql
                    .Query($"select * from [{Constants.DefaultSchema}].JobParameter where JobId = @id", new { id = jobId })
                    .ToDictionary(x => (string)x.Name, x => (string)x.Value);

                Assert.Empty(parameters);
            }, useBatching);
        }

        [Fact, CleanDatabase]
        public void GetJobData_ThrowsAnException_WhenJobIdIsNull()
        {
            UseConnection(connection => Assert.Throws<ArgumentNullException>(
                    () => connection.GetJobData(null)));
        }

        [Fact, CleanDatabase]
        public void GetJobData_ReturnsNull_WhenThereIsNoSuchJob()
        {
            UseConnection(connection =>
            {
                var result = connection.GetJobData("1");
                Assert.Null(result);
            });
        }

        [Fact, CleanDatabase]
        public void GetJobData_ReturnsResult_WhenJobExists()
        {
            var arrangeSql = $@"
insert into [{Constants.DefaultSchema}].Job (InvocationData, Arguments, StateName, CreatedAt)
values (@invocationData, @arguments, @stateName, getutcdate())
select scope_identity() as Id";

            UseConnections((sql, connection) =>
            {
                var job = Job.FromExpression(() => SampleMethod("wrong"));

                var jobId = sql.Query(
                    arrangeSql,
                    new
                    {
                        invocationData = JobHelper.ToJson(InvocationData.Serialize(job)),
                        stateName = "Succeeded",
                        arguments = "['Arguments']"
                    }).Single();

                var result = connection.GetJobData(((long)jobId.Id).ToString());

                Assert.NotNull(result);
                Assert.NotNull(result.Job);
                Assert.Equal("Succeeded", result.State);
                Assert.Equal("Arguments", result.Job.Args[0]);
                Assert.Null(result.LoadException);
                Assert.True(DateTime.UtcNow.AddMinutes(-1) < result.CreatedAt);
                Assert.True(result.CreatedAt < DateTime.UtcNow.AddMinutes(1));
            });
        }

        [Fact, CleanDatabase]
        public void GetStateData_ThrowsAnException_WhenJobIdIsNull()
        {
            UseConnection(
                connection => Assert.Throws<ArgumentNullException>(
                    () => connection.GetStateData(null)));
        }

        [Fact, CleanDatabase]
        public void GetStateData_ReturnsNull_IfThereIsNoSuchState()
        {
            UseConnection(connection =>
            {
                var result = connection.GetStateData("1");
                Assert.Null(result);
            });
        }

        [Fact, CleanDatabase]
        public void GetStateData_ReturnsCorrectData()
        {
            var arrangeSql = $@"
insert into [{Constants.DefaultSchema}].Job (InvocationData, Arguments, StateName, CreatedAt)
values ('', '', '', getutcdate());
declare @JobId bigint;
set @JobId = scope_identity();
insert into [{Constants.DefaultSchema}].State (JobId, Name, CreatedAt)
values (@JobId, 'old-state', getutcdate());
insert into [{Constants.DefaultSchema}].State (JobId, Name, Reason, Data, CreatedAt)
values (@JobId, @name, @reason, @data, getutcdate());
declare @StateId bigint;
set @StateId = scope_identity();
update [{Constants.DefaultSchema}].Job set StateId = @StateId;
select @JobId as Id;";

            UseConnections((sql, connection) =>
            {
                var data = new Dictionary<string, string>
                {
                    { "Key", "Value" }
                };

                var jobId = (long)sql.Query(
                    arrangeSql,
                    new { name = "Name", reason = "Reason", @data = JobHelper.ToJson(data) }).Single().Id;

                var result = connection.GetStateData(jobId.ToString());

                Assert.NotNull(result);

                Assert.Equal("Name", result.Name);
                Assert.Equal("Reason", result.Reason);
                Assert.Equal("Value", result.Data["Key"]);
            });
        }

        [Fact, CleanDatabase]
        public void GetStateData_ReturnsCorrectData_WhenPropertiesAreCamelcased()
        {
            var arrangeSql = $@"
insert into [{Constants.DefaultSchema}].Job (InvocationData, Arguments, StateName, CreatedAt)
values ('', '', '', getutcdate());
declare @JobId bigint;
set @JobId = scope_identity();
insert into [{Constants.DefaultSchema}].State (JobId, Name, CreatedAt)
values (@JobId, 'old-state', getutcdate());
insert into [{Constants.DefaultSchema}].State (JobId, Name, Reason, Data, CreatedAt)
values (@JobId, @name, @reason, @data, getutcdate());
declare @StateId bigint;
set @StateId = scope_identity();
update [{Constants.DefaultSchema}].Job set StateId = @StateId;
select @JobId as Id;";

            UseConnections((sql, connection) =>
            {
                var data = new Dictionary<string, string>
                {
                    { "key", "Value" }
                };

                var jobId = (long)sql.Query(
                    arrangeSql,
                    new { name = "Name", reason = "Reason", @data = JobHelper.ToJson(data) }).Single().Id;

                var result = connection.GetStateData(jobId.ToString());
                Assert.NotNull(result);

                Assert.Equal("Value", result.Data["Key"]);
            });
        }

        [Fact, CleanDatabase]
        public void GetJobData_ReturnsJobLoadException_IfThereWasADeserializationException()
        {
            var arrangeSql = $@"
insert into [{Constants.DefaultSchema}].Job (InvocationData, Arguments, StateName, CreatedAt)
values (@invocationData, @arguments, @stateName, getutcdate())
select scope_identity() as Id";

            UseConnections((sql, connection) =>
            {
                var jobId = sql.Query(
                    arrangeSql,
                    new
                    {
                        invocationData = JobHelper.ToJson(new InvocationData(null, null, null, null)),
                        stateName = "Succeeded",
                        arguments = "['Arguments']"
                    }).Single();

                var result = connection.GetJobData(((long)jobId.Id).ToString());

                Assert.NotNull(result.LoadException);
            });
        }

        [Fact, CleanDatabase]
        public void SetParameter_ThrowsAnException_WhenJobIdIsNull()
        {
            UseConnection(connection =>
            {
                var exception = Assert.Throws<ArgumentNullException>(
                    () => connection.SetJobParameter(null, "name", "value"));

                Assert.Equal("id", exception.ParamName);
            });
        }

        [Fact, CleanDatabase]
        public void SetParameter_ThrowsAnException_WhenNameIsNull()
        {
            UseConnection(connection =>
            {
                var exception = Assert.Throws<ArgumentNullException>(
                    () => connection.SetJobParameter("1", null, "value"));

                Assert.Equal("name", exception.ParamName);
            });
        }

        [Fact, CleanDatabase]
        public void SetParameters_CreatesNewParameter_WhenParameterWithTheGivenNameDoesNotExists()
        {
            var arrangeSql = $@"
insert into [{Constants.DefaultSchema}].Job (InvocationData, Arguments, CreatedAt)
values ('', '', getutcdate())
select scope_identity() as Id";

            UseConnections((sql, connection) =>
            {
                var job = sql.Query(arrangeSql).Single();
                string jobId = job.Id.ToString();

                connection.SetJobParameter(jobId, "Name", "Value");

                var parameter = sql.Query(
                    $"select * from [{Constants.DefaultSchema}].JobParameter where JobId = @id and Name = @name",
                    new { id = jobId, name = "Name" }).Single();

                Assert.Equal("Value", parameter.Value);
            });
        }

        [Fact, CleanDatabase]
        public void SetParameter_UpdatesValue_WhenParameterWithTheGivenName_AlreadyExists()
        {
            var arrangeSql = $@"
insert into [{Constants.DefaultSchema}].Job (InvocationData, Arguments, CreatedAt)
values ('', '', getutcdate())
select scope_identity() as Id";

            UseConnections((sql, connection) =>
            {
                var job = sql.Query(arrangeSql).Single();
                string jobId = job.Id.ToString();

                connection.SetJobParameter(jobId, "Name", "Value");
                connection.SetJobParameter(jobId, "Name", "AnotherValue");

                var parameter = sql.Query(
                    $"select * from [{Constants.DefaultSchema}].JobParameter where JobId = @id and Name = @name",
                    new { id = jobId, name = "Name" }).Single();

                Assert.Equal("AnotherValue", parameter.Value);
            });
        }

        [Fact, CleanDatabase]
        public void SetParameter_CanAcceptNulls_AsValues()
        {
            var arrangeSql = $@"
insert into [{Constants.DefaultSchema}].Job (InvocationData, Arguments, CreatedAt)
values ('', '', getutcdate())
select scope_identity() as Id";

            UseConnections((sql, connection) =>
            {
                var job = sql.Query(arrangeSql).Single();
                string jobId = job.Id.ToString();

                connection.SetJobParameter(jobId, "Name", null);

                var parameter = sql.Query(
                    $"select * from [{Constants.DefaultSchema}].JobParameter where JobId = @id and Name = @name",
                    new { id = jobId, name = "Name" }).Single();

                Assert.Equal((string) null, parameter.Value);
            });
        }

        [Theory, CleanDatabase]
        [InlineData(true), InlineData(false)]
        public void SetJobParameter_WithIgnoreDupKeyOption_InsertsNonExistingValue(bool useBatching)
        {
            try
            {
                UseConnections((sql, connection) =>
                {
                    sql.Execute($"ALTER TABLE [{Constants.DefaultSchema}].[JobParameter] REBUILD WITH (IGNORE_DUP_KEY = ON)");

                    string jobId = sql.Query($@"
insert into [{Constants.DefaultSchema}].Job (InvocationData, Arguments, CreatedAt) values ('', '', getutcdate())
select scope_identity() as Id").Single().Id.ToString();

                    connection.SetJobParameter(jobId, "Name", "Value");

                    var parameter = sql.Query(
                        $"select * from [{Constants.DefaultSchema}].JobParameter where JobId = @jobId and Name = N'Name'",
                        new { jobId }).Single();

                    Assert.Equal("Value", parameter.Value);
                }, useBatching);
            }
            finally
            {
                UseConnections((sql, _) => sql.Execute($"ALTER TABLE [{Constants.DefaultSchema}].[JobParameter] REBUILD WITH (IGNORE_DUP_KEY = OFF)"));
            }
        }

        [Theory, CleanDatabase]
        [InlineData(false), InlineData(true)]
        public void SetJobParameter_WithIgnoreDupKeyOption_UpdatesExistingValue_WhenIgnoreDupKeyOptionIsSet(bool setIgnoreDupKey)
        {
            try
            {
                UseConnections((sql, connection) =>
                {
                    sql.Execute("SET XACT_ABORT ON");
                    var onOrOff = setIgnoreDupKey ? "ON" : "OFF";
                    sql.Execute($"ALTER TABLE [{Constants.DefaultSchema}].[JobParameter] REBUILD WITH (IGNORE_DUP_KEY = {onOrOff})");

                    string jobId1 = sql.Query($@"
insert into [{Constants.DefaultSchema}].Job (InvocationData, Arguments, CreatedAt) values ('', '', getutcdate())
select scope_identity() as Id").Single().Id.ToString();

                    string jobId2 = sql.Query($@"
insert into [{Constants.DefaultSchema}].Job (InvocationData, Arguments, CreatedAt) values ('', '', getutcdate())
select scope_identity() as Id").Single().Id.ToString();

                    sql.Execute(
                        $@"insert into [{Constants.DefaultSchema}].[JobParameter] (JobId, Name, Value) values
(@jobId1, N'Name1', N'Value1'),
(@jobId1, N'Name2', N'Value1'),
(@jobId2, N'Name1', N'Value1')", new { jobId1, jobId2 });

                    connection.SetJobParameter(jobId1, "Name1", "Value2");

                    var parameters1 = sql.Query(
                        $"select * from [{Constants.DefaultSchema}].JobParameter where JobId = @jobId1",
                        new { jobId1 }).ToDictionary(x => (string)x.Name, x => (string)x.Value);

                    Assert.Equal("Value2", parameters1["Name1"]);
                    Assert.Equal("Value1", parameters1["Name2"]);

                    var parameters2 = sql.Query(
                        $"select * from [{Constants.DefaultSchema}].JobParameter where JobId = @jobId2",
                        new { jobId2 }).ToDictionary(x => (string)x.Name, x => (string)x.Value);

                    Assert.Equal("Value1", parameters2["Name1"]);
                });
            }
            finally
            {
                UseConnections((sql, _) => sql.Execute($"ALTER TABLE [{Constants.DefaultSchema}].[JobParameter] REBUILD WITH (IGNORE_DUP_KEY = OFF)"));
            }
        }

        [Fact, CleanDatabase]
        public void GetParameter_ThrowsAnException_WhenJobIdIsNull()
        {
            UseConnection(connection =>
            {
                var exception = Assert.Throws<ArgumentNullException>(
                    () => connection.GetJobParameter(null, "hello"));

                Assert.Equal("id", exception.ParamName);
            });
        }

        [Fact, CleanDatabase]
        public void GetParameter_ThrowsAnException_WhenNameIsNull()
        {
            UseConnection(connection =>
            {
                var exception = Assert.Throws<ArgumentNullException>(
                    () => connection.GetJobParameter("1", null));

                Assert.Equal("name", exception.ParamName);
            });
        }

        [Fact, CleanDatabase]
        public void GetParameter_ReturnsNull_WhenParameterDoesNotExists()
        {
            UseConnection(connection =>
            {
                var value = connection.GetJobParameter("1", "hello");
                Assert.Null(value);
            });
        }

        [Fact, CleanDatabase]
        public void GetParameter_ReturnsParameterValue_WhenJobExists()
        {
            var arrangeSql = $@"
declare @id bigint
insert into [{Constants.DefaultSchema}].Job (InvocationData, Arguments, CreatedAt)
values ('', '', getutcdate())
set @id = scope_identity()
insert into [{Constants.DefaultSchema}].JobParameter (JobId, Name, Value)
values (@id, @name, @value)
select @id";

            UseConnections((sql, connection) =>
            {
                var id = sql.Query<long>(
                    arrangeSql,
                    new { name = "name", value = "value" }).Single();

                var value = connection.GetJobParameter(id.ToString(), "name");

                Assert.Equal("value", value);
            });
        }

        [Fact, CleanDatabase]
        public void GetFirstByLowestScoreFromSet_ThrowsAnException_WhenKeyIsNull()
        {
            UseConnection(connection =>
            {
                var exception = Assert.Throws<ArgumentNullException>(
                    () => connection.GetFirstByLowestScoreFromSet(null, 0, 1));

                Assert.Equal("key", exception.ParamName);
            });
        }

        [Fact, CleanDatabase]
        public void GetFirstByLowestScoreFromSet_ThrowsAnException_ToScoreIsLowerThanFromScore()
        {
            UseConnection(connection =>
            {
                var exception = Assert.Throws<ArgumentException>(
                    () => connection.GetFirstByLowestScoreFromSet("key", 0, -1));

                Assert.Equal("toScore", exception.ParamName);
            });
        }

        [Fact, CleanDatabase]
        public void GetFirstByLowestScoreFromSet_ReturnsNull_WhenTheKeyDoesNotExist()
        {
            UseConnection(connection =>
            {
                var result = connection.GetFirstByLowestScoreFromSet(
                    "key", 0, 1);

                Assert.Null(result);
            });
        }

        [Fact, CleanDatabase]
        public void GetFirstByLowestScoreFromSet_ThrowsArgException_WhenRequestingLessThanZero()
        {
            UseConnection(connection =>
            {
                var exception = Assert.Throws<ArgumentException>(
                    () => connection.GetFirstByLowestScoreFromSet("key", 0, 1, -1));

                Assert.Equal("count", exception.ParamName);
            });
        }

        [Fact]
        public void GetFirstByLowestScoreFromSet_ReturnsEmpty_WhenNoneExist()
        {
            UseConnection(connection =>
            {
                var result = connection.GetFirstByLowestScoreFromSet("key", 0, 1, 10);
                Assert.Empty(result);
            });
        }
        
        [Fact, CleanDatabase]
        public void GetFirstByLowestScoreFromSet_ReturnsN_WhenMoreThanNExist()
        {
            var arrangeSql = $@"
insert into [{Constants.DefaultSchema}].[Set] ([Key], Score, Value)
values 
('key', 1.0, '1234'),
('key', -1.0, '567'),
('key', -5.0, '890'),
('another-key', -2.0, 'abcd')";
            
            UseConnections((sql, connection) =>
            {
                sql.Execute(arrangeSql);
                
                var result = connection.GetFirstByLowestScoreFromSet("key", -10.0, 10.0, 2);
                
                Assert.Equal(2, result.Count);
                Assert.Equal("890", result.ElementAt(0));
                Assert.Equal("567", result.ElementAt(1));
            });
        }
        
        [Fact, CleanDatabase]
        public void GetFirstByLowestScoreFromSet_ReturnsN_WhenMoreThanNExist_And_RequestedCountIsGreaterThanN()
        {
            var arrangeSql = $@"
insert into [{Constants.DefaultSchema}].[Set] ([Key], Score, Value)
values 
('key', 1.0, '1234'),
('key', -1.0, '567'),
('key', -5.0, '890'),
('another-key', -2.0, 'abcd')";
            
            UseConnections((sql, connection) =>
            {
                sql.Execute(arrangeSql);
                
                var result = connection.GetFirstByLowestScoreFromSet("another-key", -10.0, 10.0, 5);
                
                Assert.Equal(1, result.Count);
                Assert.Equal("abcd", result.First());
            });
        }

        [Fact, CleanDatabase]
        public void GetFirstByLowestScoreFromSet_ReturnsTheValueWithTheLowestScore()
        {
            var arrangeSql = $@"
insert into [{Constants.DefaultSchema}].[Set] ([Key], Score, Value)
values 
('key', 1.0, '1.0'),
('key', -1.0, '-1.0'),
('key', -5.0, '-5.0'),
('another-key', -2.0, '-2.0')";

            UseConnections((sql, connection) =>
            {
                sql.Execute(arrangeSql);

                var result = connection.GetFirstByLowestScoreFromSet("key", -1.0, 3.0);
                
                Assert.Equal("-1.0", result);
            });
        }

        [Fact, CleanDatabase]
        public void AnnounceServer_ThrowsAnException_WhenServerIdIsNull()
        {
            UseConnection(connection =>
            {
                var exception = Assert.Throws<ArgumentNullException>(
                    () => connection.AnnounceServer(null, new ServerContext()));

                Assert.Equal("serverId", exception.ParamName);
            });
        }

        [Fact, CleanDatabase]
        public void AnnounceServer_ThrowsAnException_WhenContextIsNull()
        {
            UseConnection(connection =>
            {
                var exception = Assert.Throws<ArgumentNullException>(
                    () => connection.AnnounceServer("server", null));

                Assert.Equal("context", exception.ParamName);
            });
        }

        [Fact, CleanDatabase]
        public void AnnounceServer_CreatesOrUpdatesARecord()
        {
            UseConnections((sql, connection) =>
            {
                var context1 = new ServerContext
                {
                    Queues = new[] { "critical", "default" },
                    WorkerCount = 4
                };
                connection.AnnounceServer("server", context1);

                var server = sql.Query($"select * from [{Constants.DefaultSchema}].Server").Single();
                Assert.Equal("server", server.Id);
                Assert.True(((string)server.Data).StartsWith(
                    "{\"WorkerCount\":4,\"Queues\":[\"critical\",\"default\"],\"StartedAt\":"),
                    server.Data);
                Assert.NotNull(server.LastHeartbeat);

                var context2 = new ServerContext
                {
                    Queues = new[] { "default" },
                    WorkerCount = 1000 
                };
                connection.AnnounceServer("server", context2);
                var sameServer = sql.Query($"select * from [{Constants.DefaultSchema}].Server").Single();
                Assert.Equal("server", sameServer.Id);
                Assert.Contains("1000", sameServer.Data);
            });
        }

        [Fact, CleanDatabase]
        public void RemoveServer_ThrowsAnException_WhenServerIdIsNull()
        {
            UseConnection(connection => Assert.Throws<ArgumentNullException>(
                () => connection.RemoveServer(null)));
        }

        [Fact, CleanDatabase]
        public void RemoveServer_RemovesAServerRecord()
        {
            var arrangeSql = $@"
insert into [{Constants.DefaultSchema}].Server (Id, Data, LastHeartbeat)
values 
('Server1', '', getutcdate()),
('Server2', '', getutcdate())";

            UseConnections((sql, connection) =>
            {
                sql.Execute(arrangeSql);

                connection.RemoveServer("Server1");

                var server = sql.Query($"select * from [{Constants.DefaultSchema}].Server").Single();
                Assert.NotEqual("Server1", server.Id, StringComparer.OrdinalIgnoreCase);
            });
        }

        [Fact, CleanDatabase]
        public void Heartbeat_ThrowsAnException_WhenServerIdIsNull()
        {
            UseConnection(connection => Assert.Throws<ArgumentNullException>(
                () => connection.Heartbeat(null)));
        }

        [Fact, CleanDatabase]
        public void Heartbeat_UpdatesLastHeartbeat_OfTheServerWithGivenId()
        {
            var arrangeSql = $@"
insert into [{Constants.DefaultSchema}].Server (Id, Data, LastHeartbeat)
values
('server1', '', '2012-12-12 12:12:12'),
('server2', '', '2012-12-12 12:12:12')";

            UseConnections((sql, connection) =>
            {
                sql.Execute(arrangeSql);

                connection.Heartbeat("server1");

                var servers = sql.Query($"select * from [{Constants.DefaultSchema}].Server")
                    .ToDictionary(x => (string)x.Id, x => (DateTime)x.LastHeartbeat);

                Assert.NotEqual(2012, servers["server1"].Year);
                Assert.Equal(2012, servers["server2"].Year);
            });
        }

        [Fact, CleanDatabase]
        public void RemoveTimedOutServers_ThrowsAnException_WhenTimeOutIsNegative()
        {
            UseConnection(connection => Assert.Throws<ArgumentException>(
                () => connection.RemoveTimedOutServers(TimeSpan.FromMinutes(-5))));
        }

        [Fact, CleanDatabase]
        public void RemoveTimedOutServers_DoItsWorkPerfectly()
        {
            var arrangeSql = $@"
insert into [{Constants.DefaultSchema}].Server (Id, Data, LastHeartbeat)
values (@id, '', @heartbeat)";

            UseConnections((sql, connection) =>
            {
                sql.Execute(
                    arrangeSql,
                    new[]
                    {
                        new { id = "server1", heartbeat = DateTime.UtcNow.AddDays(-1) },
                        new { id = "server2", heartbeat = DateTime.UtcNow.AddHours(-12) }
                    });

                connection.RemoveTimedOutServers(TimeSpan.FromHours(15));

                var liveServer = sql.Query($"select * from [{Constants.DefaultSchema}].Server").Single();
                Assert.Equal("server2", liveServer.Id);
            });
        }

        [Fact, CleanDatabase]
        public void GetAllItemsFromSet_ThrowsAnException_WhenKeyIsNull()
        {
            UseConnection(connection =>
                Assert.Throws<ArgumentNullException>(() => connection.GetAllItemsFromSet(null)));
        }

        [Fact, CleanDatabase]
        public void GetAllItemsFromSet_ReturnsEmptyCollection_WhenKeyDoesNotExist()
        {
            UseConnection(connection =>
            {
                var result = connection.GetAllItemsFromSet("some-set");

                Assert.NotNull(result);
                Assert.Equal(0, result.Count);
            });
        }

        [Fact, CleanDatabase]
        public void GetAllItemsFromSet_ReturnsAllItems()
        {
            var arrangeSql = $@"
insert into [{Constants.DefaultSchema}].[Set] ([Key], Score, Value)
values (@key, 0.0, @value)";

            UseConnections((sql, connection) =>
            {
                // Arrange
                sql.Execute(arrangeSql, new[]
                {
                    new { key = "some-set", value = "1" },
                    new { key = "some-set", value = "2" },
                    new { key = "another-set", value = "3" }
                });

                // Act
                var result = connection.GetAllItemsFromSet("some-set");

                // Assert
                Assert.Equal(2, result.Count);
                Assert.Contains("1", result);
                Assert.Contains("2", result);
            });
        }

        [Fact, CleanDatabase]
        public void SetRangeInHash_ThrowsAnException_WhenKeyIsNull()
        {
            UseConnection(connection =>
            {
                var exception = Assert.Throws<ArgumentNullException>(
                    () => connection.SetRangeInHash(null, new Dictionary<string, string>()));

                Assert.Equal("key", exception.ParamName);
            });
        }

        [Fact, CleanDatabase]
        public void SetRangeInHash_ThrowsAnException_WhenKeyValuePairsArgumentIsNull()
        {
            UseConnection(connection =>
            {
                var exception = Assert.Throws<ArgumentNullException>(
                    () => connection.SetRangeInHash("some-hash", null));

                Assert.Equal("keyValuePairs", exception.ParamName);
            });
        }

        [Theory, CleanDatabase]
        [InlineData(true)]
        [InlineData(false)]
        public void SetRangeInHash_MergesAllRecords(bool useBatching)
        {
            UseConnections((sql, connection) =>
            {
                connection.SetRangeInHash("some-hash", new Dictionary<string, string>
                {
                    { "Key1", "Value1" },
                    { "Key2", "Value2" }
                });

                var result = sql.Query(
                    $"select * from [{Constants.DefaultSchema}].Hash where [Key] = @key",
                    new { key = "some-hash" })
                    .ToDictionary(x => (string)x.Field, x => (string)x.Value);

                Assert.Equal("Value1", result["Key1"]);
                Assert.Equal("Value2", result["Key2"]);
            }, useBatching);
        }

        [Theory, CleanDatabase]
        [InlineData(true)]
        [InlineData(false)]
        public void SetRangeInHash_CanCreateFieldsWithNullValues(bool useBatching)
        {
            UseConnections((sql, connection) =>
            {
                connection.SetRangeInHash("some-hash", new Dictionary<string, string>
                {
                    { "Key1", null }
                });

                var result = sql.Query(
                        $"select * from [{Constants.DefaultSchema}].Hash where [Key] = @key",
                        new { key = "some-hash" })
                    .ToDictionary(x => (string)x.Field, x => (string)x.Value);

                Assert.Equal(null, result["Key1"]);
            }, useBatching);
        }

        [Fact, CleanDatabase]
        public void SetRangeInHash_ReleasesTheAcquiredLock()
        {
            UseConnections((sql, connection) =>
            {
                connection.SetRangeInHash("some-hash", new Dictionary<string, string>
                {
                    { "Key", "Value" }
                });

                var result = sql.QuerySingle<string>($"select APPLOCK_MODE( 'public' , 'HangFire:Hash:Lock' , 'Session' )");
                Assert.Equal("NoLock", result);
            });
        }

        [Theory, CleanDatabase]
        [InlineData(true), InlineData(false)]
        public void SetRangeInHash_WithIgnoreDupKeyOption_InsertsNonExistingValue(bool useBatching)
        {
            try
            {
                UseConnections((sql, connection) =>
                {
                    sql.Execute($"ALTER TABLE [{Constants.DefaultSchema}].[Hash] REBUILD WITH (IGNORE_DUP_KEY = ON)");

                    connection.SetRangeInHash("some-hash", new Dictionary<string, string>
                    {
                        { "key", "value" }
                    });

                    var result = sql
                        .Query($"select * from [{Constants.DefaultSchema}].Hash where [Key] = N'some-hash'")
                        .ToDictionary(x => (string)x.Field, x => (string)x.Value);

                    Assert.Equal("value", result["key"]);
                }, useBatching);
            }
            finally
            {
                UseConnections((sql, _) => sql.Execute($"ALTER TABLE [{Constants.DefaultSchema}].[Hash] REBUILD WITH (IGNORE_DUP_KEY = OFF)"));
            }
        }

        [Theory, CleanDatabase]
        [InlineData(true), InlineData(false)]
        public void SetRangeInHash_WithIgnoreDupKeyOption_UpdatesExistingValue_WhenIgnoreDupKeyOptionIsSet(bool setIgnoreDupKey)
        {
            try
            {
                UseConnections((sql, connection) =>
                {
                    var onOrOff = setIgnoreDupKey ? "ON" : "OFF";
                    sql.Execute($"ALTER TABLE [{Constants.DefaultSchema}].[Hash] REBUILD WITH (IGNORE_DUP_KEY = {onOrOff})");
                    sql.Execute($@"insert into [{Constants.DefaultSchema}].Hash([Key], Field, Value) VALUES
(N'some-hash', N'key1', N'value1'),
(N'some-hash', N'key2', N'value1'),
(N'othr-hash', N'key1', N'value1')");

                    connection.SetRangeInHash("some-hash", new Dictionary<string, string>
                    {
                        { "key1", "value2" }
                    });

                    var someResult = sql
                        .Query($"select * from [{Constants.DefaultSchema}].Hash where [Key] = N'some-hash'")
                        .ToDictionary(x => (string)x.Field, x => (string)x.Value);

                    Assert.Equal("value2", someResult["key1"]);
                    Assert.Equal("value1", someResult["key2"]);

                    var othrResult = sql
                        .Query($"select * from [{Constants.DefaultSchema}].Hash where [Key] = N'othr-hash'")
                        .ToDictionary(x => (string)x.Field, x => (string)x.Value);

                    Assert.Equal("value1", othrResult["key1"]);
                });
            }
            finally
            {
                UseConnections((sql, _) => sql.Execute($"ALTER TABLE [{Constants.DefaultSchema}].[Hash] REBUILD WITH (IGNORE_DUP_KEY = OFF)"));
            }
        }

        [Fact, CleanDatabase]
        public void GetAllEntriesFromHash_ThrowsAnException_WhenKeyIsNull()
        {
            UseConnection(connection =>
                Assert.Throws<ArgumentNullException>(() => connection.GetAllEntriesFromHash(null)));
        }

        [Fact, CleanDatabase]
        public void GetAllEntriesFromHash_ReturnsNull_IfHashDoesNotExist()
        {
            UseConnection(connection =>
            {
                var result = connection.GetAllEntriesFromHash("some-hash");
                Assert.Null(result);
            });
        }

        [Fact, CleanDatabase]
        public void GetAllEntriesFromHash_ReturnsAllKeysAndTheirValues()
        {
            var arrangeSql = $@"
insert into [{Constants.DefaultSchema}].Hash ([Key], [Field], [Value])
values (@key, @field, @value)";

            UseConnections((sql, connection) =>
            {
                // Arrange
                sql.Execute(arrangeSql, new[]
                {
                    new { key = "some-hash", field = "Key1", value = "Value1" },
                    new { key = "some-hash", field = "Key2", value = "Value2" },
                    new { key = "another-hash", field = "Key3", value = "Value3" }
                });

                // Act
                var result = connection.GetAllEntriesFromHash("some-hash");

                // Assert
                Assert.NotNull(result);
                Assert.Equal(2, result.Count);
                Assert.Equal("Value1", result["Key1"]);
                Assert.Equal("Value2", result["Key2"]);
            });
        }

        [Fact, CleanDatabase]
        public void GetSetCount_ThrowsAnException_WhenKeyIsNull()
        {
            UseConnection(connection =>
            {
                Assert.Throws<ArgumentNullException>(
                    () => connection.GetSetCount(null));
            });
        }

        [Fact, CleanDatabase]
        public void GetSetCount_ReturnsZero_WhenSetDoesNotExist()
        {
            UseConnection(connection =>
            {
                var result = connection.GetSetCount("my-set");
                Assert.Equal(0, result);
            });
        }

        [Fact, CleanDatabase]
        public void GetSetCount_ReturnsNumberOfElements_InASet()
        {
            var arrangeSql = $@"
insert into [{Constants.DefaultSchema}].[Set] ([Key], [Value], [Score])
values (@key, @value, 0.0)";

            UseConnections((sql, connection) =>
            {
                sql.Execute(arrangeSql, new List<dynamic>
                {
                    new { key = "set-1", value = "value-1" },
                    new { key = "set-2", value = "value-1" },
                    new { key = "set-1", value = "value-2" }
                });

                var result = connection.GetSetCount("set-1");

                Assert.Equal(2, result);
            });
        }

        [Fact, CleanDatabase]
        public void GetRangeFromSet_ThrowsAnException_WhenKeyIsNull()
        {
            UseConnection(connection =>
            {
                Assert.Throws<ArgumentNullException>(() => connection.GetRangeFromSet(null, 0, 1));
            });
        }

        [Fact, CleanDatabase]
        public void GetRangeFromSet_ReturnsPagedElements()
        {
            var arrangeSql = $@"
insert into [{Constants.DefaultSchema}].[Set] ([Key], [Value], [Score])
values (@Key, @Value, 0.0)";

            UseConnections((sql, connection) =>
            {
                sql.Execute(arrangeSql, new List<dynamic>
                {
                    new { Key = "set-1", Value = "1" },
                    new { Key = "set-1", Value = "2" },
                    new { Key = "set-1", Value = "3" },
                    new { Key = "set-1", Value = "4" },
                    new { Key = "set-2", Value = "4" },
                    new { Key = "set-1", Value = "5" }
                });

                var result = connection.GetRangeFromSet("set-1", 2, 3);

                Assert.Equal(new [] { "3", "4" }, result);
            });
        }

        [Fact, CleanDatabase]
        public void GetCounter_ThrowsAnException_WhenKeyIsNull()
        {
            UseConnection(connection =>
            {
                Assert.Throws<ArgumentNullException>(
                    () => connection.GetCounter(null));
            });
        }

        [Fact, CleanDatabase]
        public void GetCounter_ReturnsZero_WhenKeyDoesNotExist()
        {
            UseConnection(connection =>
            {
                var result = connection.GetCounter("my-counter");
                Assert.Equal(0, result);
            });
        }

        [Fact, CleanDatabase]
        public void GetCounter_ReturnsSumOfValues_InCounterTable()
        {
            var arrangeSql = $@"
insert into [{Constants.DefaultSchema}].Counter ([Key], [Value])
values (@key, @value)";

            UseConnections((sql, connection) =>
            {
                // Arrange
                sql.Execute(arrangeSql, new[]
                {
                    new { key = "counter-1", value = 1 },
                    new { key = "counter-2", value = 1 },
                    new { key = "counter-1", value = 1 }
                });

                // Act
                var result = connection.GetCounter("counter-1");

                // Assert
                Assert.Equal(2, result);
            });
        }

        [Fact, CleanDatabase]
        public void GetCounter_IncludesValues_FromCounterAggregateTable()
        {
            var arrangeSql = $@"
insert into [{Constants.DefaultSchema}].AggregatedCounter ([Key], [Value])
values (@key, @value)";

            UseConnections((sql, connection) =>
            {
                // Arrange
                sql.Execute(arrangeSql, new[]
                {
                    new { key = "counter-1", value = 12 },
                    new { key = "counter-2", value = 15 }
                });

                // Act
                var result = connection.GetCounter("counter-1");

                Assert.Equal(12, result);
            });
        }

        [Fact, CleanDatabase]
        public void GetHashCount_ThrowsAnException_WhenKeyIsNull()
        {
            UseConnection(connection =>
            {
                Assert.Throws<ArgumentNullException>(() => connection.GetHashCount(null));
            });
        }

        [Fact, CleanDatabase]
        public void GetHashCount_ReturnsZero_WhenKeyDoesNotExist()
        {
            UseConnection(connection =>
            {
                var result = connection.GetHashCount("my-hash");
                Assert.Equal(0, result);
            });
        }

        [Fact, CleanDatabase]
        public void GetHashCount_ReturnsNumber_OfHashFields()
        {
            var arrangeSql = $@"
insert into [{Constants.DefaultSchema}].Hash ([Key], [Field])
values (@key, @field)";

            UseConnections((sql, connection) =>
            {
                // Arrange
                sql.Execute(arrangeSql, new[]
                {
                    new { key = "hash-1", field = "field-1" },
                    new { key = "hash-1", field = "field-2" },
                    new { key = "hash-2", field = "field-1" }
                });

                // Act
                var result = connection.GetHashCount("hash-1");

                // Assert
                Assert.Equal(2, result);
            });
        }

        [Fact, CleanDatabase]
        public void GetHashTtl_ThrowsAnException_WhenKeyIsNull()
        {
            UseConnection(connection =>
            {
                Assert.Throws<ArgumentNullException>(
                    () => connection.GetHashTtl(null));
            });
        }

        [Fact, CleanDatabase]
        public void GetHashTtl_ReturnsNegativeValue_WhenHashDoesNotExist()
        {
            UseConnection(connection =>
            {
                var result = connection.GetHashTtl("my-hash");
                Assert.True(result < TimeSpan.Zero);
            });
        }

        [Fact, CleanDatabase]
        public void GetHashTtl_ReturnsExpirationTimeForHash()
        {
            var arrangeSql = $@"
insert into [{Constants.DefaultSchema}].Hash ([Key], [Field], [ExpireAt])
values (@key, @field, @expireAt)";

            UseConnections((sql, connection) =>
            {
                // Arrange
                sql.Execute(arrangeSql, new[]
                {
                    new { key = "hash-1", field = "field", expireAt = (DateTime?)DateTime.UtcNow.AddHours(1) },
                    new { key = "hash-2", field = "field", expireAt = (DateTime?) null }
                });

                // Act
                var result = connection.GetHashTtl("hash-1");

                // Assert
                Assert.True(TimeSpan.FromMinutes(59) < result);
                Assert.True(result < TimeSpan.FromMinutes(61));
            });
        }

        [Fact, CleanDatabase]
        public void GetListCount_ThrowsAnException_WhenKeyIsNull()
        {
            UseConnection(connection =>
            {
                Assert.Throws<ArgumentNullException>(
                    () => connection.GetListCount(null));
            });
        }

        [Fact, CleanDatabase]
        public void GetListCount_ReturnsZero_WhenListDoesNotExist()
        {
            UseConnection(connection =>
            {
                var result = connection.GetListCount("my-list");
                Assert.Equal(0, result);
            });
        }

        [Fact, CleanDatabase]
        public void GetListCount_ReturnsTheNumberOfListElements()
        {
            var arrangeSql = $@"
insert into [{Constants.DefaultSchema}].List ([Key])
values (@key)";

            UseConnections((sql, connection) =>
            {
                // Arrange
                sql.Execute(arrangeSql, new[]
                {
                    new { key = "list-1" },
                    new { key = "list-1" },
                    new { key = "list-2" }
                });

                // Act
                var result = connection.GetListCount("list-1");

                // Assert
                Assert.Equal(2, result);
            });
        }

        [Fact, CleanDatabase]
        public void GetListTtl_ThrowsAnException_WhenKeyIsNull()
        {
            UseConnection(connection =>
            {
                Assert.Throws<ArgumentNullException>(
                    () => connection.GetListTtl(null));
            });
        }

        [Fact, CleanDatabase]
        public void GetListTtl_ReturnsNegativeValue_WhenListDoesNotExist()
        {
            UseConnection(connection =>
            {
                var result = connection.GetListTtl("my-list");
                Assert.True(result < TimeSpan.Zero);
            });
        }

        [Fact, CleanDatabase]
        public void GetListTtl_ReturnsExpirationTimeForList()
        {
            var arrangeSql = $@"
insert into [{Constants.DefaultSchema}].List ([Key], [ExpireAt])
values (@key, @expireAt)";

            UseConnections((sql, connection) =>
            {
                // Arrange
                sql.Execute(arrangeSql, new[]
                {
                    new { key = "list-1", expireAt = (DateTime?) DateTime.UtcNow.AddHours(1) },
                    new { key = "list-2", expireAt = (DateTime?) null }
                });

                // Act
                var result = connection.GetListTtl("list-1");

                // Assert
                Assert.True(TimeSpan.FromMinutes(59) < result);
                Assert.True(result < TimeSpan.FromMinutes(61));
            });
        }

        [Fact, CleanDatabase]
        public void GetValueFromHash_ThrowsAnException_WhenKeyIsNull()
        {
            UseConnection(connection =>
            {
                var exception = Assert.Throws<ArgumentNullException>(
                    () => connection.GetValueFromHash(null, "name"));

                Assert.Equal("key", exception.ParamName);
            });
        }

        [Fact, CleanDatabase]
        public void GetValueFromHash_ThrowsAnException_WhenNameIsNull()
        {
            UseConnection(connection =>
            {
                var exception = Assert.Throws<ArgumentNullException>(
                    () => connection.GetValueFromHash("key", null));

                Assert.Equal("name", exception.ParamName);
            });
        }

        [Fact, CleanDatabase]
        public void GetValueFromHash_ReturnsNull_WhenHashDoesNotExist()
        {
            UseConnection(connection =>
            {
                var result = connection.GetValueFromHash("my-hash", "name");
                Assert.Null(result);
            });
        }

        [Fact, CleanDatabase]
        public void GetValueFromHash_ReturnsValue_OfAGivenField()
        {
            var arrangeSql = $@"
insert into [{Constants.DefaultSchema}].Hash ([Key], [Field], [Value])
values (@key, @field, @value)";

            UseConnections((sql, connection) =>
            {
                // Arrange
                sql.Execute(arrangeSql, new[]
                {
                    new { key = "hash-1", field = "field-1", value = "1" },
                    new { key = "hash-1", field = "field-2", value = "2" },
                    new { key = "hash-2", field = "field-1", value = "3" }
                });

                // Act
                var result = connection.GetValueFromHash("hash-1", "field-1");

                // Assert
                Assert.Equal("1", result);
            });
        }

        [Fact, CleanDatabase]
        public void GetRangeFromList_ThrowsAnException_WhenKeyIsNull()
        {
            UseConnection(connection =>
            {
                var exception = Assert.Throws<ArgumentNullException>(
                    () => connection.GetRangeFromList(null, 0, 1));

                Assert.Equal("key", exception.ParamName);
            });
        }

        [Fact, CleanDatabase]
        public void GetRangeFromList_ReturnsAnEmptyList_WhenListDoesNotExist()
        {
            UseConnection(connection =>
            {
                var result = connection.GetRangeFromList("my-list", 0, 1);
                Assert.Empty(result);
            });
        }

        [Fact, CleanDatabase]
        public void GetRangeFromList_ReturnsAllEntries_WithinGivenBounds()
        {
            var arrangeSql = $@"
insert into [{Constants.DefaultSchema}].List ([Key], [Value])
values (@key, @value)";

            UseConnections((sql, connection) =>
            {
                // Arrange
                sql.Execute(arrangeSql, new[]
                {
                    new { key = "list-1", value = "1" },
                    new { key = "list-2", value = "2" },
                    new { key = "list-1", value = "3" },
                    new { key = "list-1", value = "4" },
                    new { key = "list-1", value = "5" }
                });

                // Act
                var result = connection.GetRangeFromList("list-1", 1, 2);
                
                // Assert
                Assert.Equal(new [] { "4", "3" }, result);
            });
        }

        [Fact, CleanDatabase]
        public void GetAllItemsFromList_ThrowsAnException_WhenKeyIsNull()
        {
            UseConnection(connection =>
            {
                Assert.Throws<ArgumentNullException>(
                    () => connection.GetAllItemsFromList(null));
            });
        }

        [Fact, CleanDatabase]
        public void GetAllItemsFromList_ReturnsAnEmptyList_WhenListDoesNotExist()
        {
            UseConnection(connection =>
            {
                var result = connection.GetAllItemsFromList("my-list");
                Assert.Empty(result);
            });
        }

        [Fact, CleanDatabase]
        public void GetAllItemsFromList_ReturnsAllItems_FromAGivenList()
        {
            var arrangeSql = $@"
insert into [{Constants.DefaultSchema}].List ([Key], Value)
values (@key, @value)";

            UseConnections((sql, connection) =>
            {
                // Arrange
                sql.Execute(arrangeSql, new[]
                {
                    new { key = "list-1", value = "1" },
                    new { key = "list-2", value = "2" },
                    new { key = "list-1", value = "3" }
                });

                // Act
                var result = connection.GetAllItemsFromList("list-1");

                // Assert
                Assert.Equal(new [] { "3", "1" }, result);
            });
        }

        [Fact, CleanDatabase]
        public void GetSetTtl_ThrowsAnException_WhenKeyIsNull()
        {
            UseConnection(connection =>
            {
                Assert.Throws<ArgumentNullException>(() => connection.GetSetTtl(null));
            });
        }

        [Fact, CleanDatabase]
        public void GetSetTtl_ReturnsNegativeValue_WhenSetDoesNotExist()
        {
            UseConnection(connection =>
            {
                var result = connection.GetSetTtl("my-set");
                Assert.True(result < TimeSpan.Zero);
            });
        }

        [Fact, CleanDatabase]
        public void GetSetTtl_ReturnsExpirationTime_OfAGivenSet()
        {
            var arrangeSql = $@"
insert into [{Constants.DefaultSchema}].[Set] ([Key], [Value], [ExpireAt], [Score])
values (@key, @value, @expireAt, 0.0)";

            UseConnections((sql, connection) =>
            {
                // Arrange
                sql.Execute(arrangeSql, new[]
                {
                    new { key = "set-1", value = "1", expireAt = (DateTime?) DateTime.UtcNow.AddMinutes(60) },
                    new { key = "set-2", value = "2", expireAt = (DateTime?) null }
                });

                // Act
                var result = connection.GetSetTtl("set-1");

                // Assert
                Assert.True(TimeSpan.FromMinutes(59) < result);
                Assert.True(result < TimeSpan.FromMinutes(61));
            });
        }

        [Fact, CleanDatabase]
        public void GetJobData_ReturnsResult_WhenJobIdIsLongValue()
        {
            var arrangeSql = $@"
SET IDENTITY_INSERT [{Constants.DefaultSchema}].Job ON;
insert into [{Constants.DefaultSchema}].Job (Id, InvocationData, Arguments, StateName, CreatedAt)
values (@jobId, @invocationData, '[''Arguments'']', 'Succeeded', getutcdate());";

            UseConnections((sql, connection) =>
            {
                var job = Job.FromExpression(() => SampleMethod("hello"));

                sql.Query(
                    arrangeSql,
                    new
                    {
                        jobId = int.MaxValue + 1L,
                        invocationData = JobHelper.ToJson(InvocationData.Serialize(job)),
                    });

                var result = connection.GetJobData((int.MaxValue + 1L).ToString());

                Assert.NotNull(result);
                Assert.NotNull(result.Job);
            });
        }

        [Fact, CleanDatabase]
        public void GetStateData_ReturnsCorrectData_WhenJobIdAndStateIdAreLongValues()
        {
            var arrangeSql = $@"
SET IDENTITY_INSERT [{Constants.DefaultSchema}].Job ON
insert into [{Constants.DefaultSchema}].Job (Id, InvocationData, Arguments, StateName, CreatedAt)
values (@jobId, '', '', '', getutcdate());
insert into [{Constants.DefaultSchema}].State (JobId, Name, CreatedAt)
values (@jobId, 'old-state', getutcdate());
SET IDENTITY_INSERT [{Constants.DefaultSchema}].Job OFF
SET IDENTITY_INSERT [{Constants.DefaultSchema}].State ON
insert into [{Constants.DefaultSchema}].State (Id, JobId, Name, Data, CreatedAt)
values (@stateId, @jobId, 'Name', @data, getutcdate());
update [{Constants.DefaultSchema}].Job set StateId = @stateId;";

            UseConnections((sql, connection) =>
            {
                var data = new Dictionary<string, string>
                {
                    { "Key", "Value" }
                };

                sql.Query(
                    arrangeSql,
                    new
                    {
                        jobId = int.MaxValue + 1L,
                        stateId = int.MaxValue + 1L,
                        data = JobHelper.ToJson(data)
                    });

                var result = connection.GetStateData((int.MaxValue + 1L).ToString());

                Assert.NotNull(result);
            });
        }

        [Fact, CleanDatabase]
        public void CreateExpiredJob_HandlesJobIdCanExceedInt32Max()
        {
            UseConnections((sql, connection) =>
            {
                // Arrange
                sql.Query($"DBCC CHECKIDENT('[{Constants.DefaultSchema}].Job', RESEED, {int.MaxValue + 1L});");

                // Act
                var createdAt = new DateTime(2012, 12, 12);
                var jobId = connection.CreateExpiredJob(
                    Job.FromExpression(() => SampleMethod("Hello")),
                    new Dictionary<string, string>(),
                    createdAt,
                    TimeSpan.FromDays(1));

                // Assert
                Assert.True(int.MaxValue < long.Parse(jobId));
            });
        }

        [Fact, CleanDatabase]
        public void SetJobParameter_CreatesNewParameter_WhenJobIdIsLongValue()
        {
            var arrangeSql = $@"
SET IDENTITY_INSERT [{Constants.DefaultSchema}].Job ON
insert into [{Constants.DefaultSchema}].Job (Id, InvocationData, Arguments, CreatedAt)
values (@jobId, '', '', getutcdate())";

            UseConnections((sql, connection) =>
            {
                sql.Query(
                    arrangeSql,
                    new { jobId =  int.MaxValue + 1L});

                connection.SetJobParameter((int.MaxValue + 1L).ToString(), "Name", "Value");

                var parameter = sql.Query(
                    $"select * from [{Constants.DefaultSchema}].JobParameter where JobId = @id and Name = @name",
                    new { id = int.MaxValue + 1L, name = "Name" }).Single();

                Assert.NotNull(parameter);
            });
        }

        [Fact, CleanDatabase]
        public void GetJobParameter_ReturnsParameterValue_WhenJobIdIsLong()
        {
            var arrangeSql = $@"
SET IDENTITY_INSERT [{Constants.DefaultSchema}].Job ON
insert into [{Constants.DefaultSchema}].Job (Id, InvocationData, Arguments, CreatedAt)
values (@jobId, '', '', getutcdate())
SET IDENTITY_INSERT [{Constants.DefaultSchema}].Job OFF
insert into [{Constants.DefaultSchema}].JobParameter (JobId, Name, Value)
values (@jobId, @name, @value)";

            UseConnections((sql, connection) =>
            {
                sql.Query(
                    arrangeSql, 
                    new
                    {
                        jobId = int.MaxValue + 1L,
                        name = "name", value = "value"
                    });

                var value = connection.GetJobParameter((int.MaxValue + 1L).ToString(), "name");

                Assert.Equal("value", value);
            });
        }

        [Fact, CleanSerializerSettings]
        public void HandlesChangingProcessOfStateDataSerialization()
        {
            GlobalConfiguration.Configuration.UseSerializerSettings(SerializerSettingsHelper.DangerousSettings);
            var stateData = new Dictionary<string, string>
            {
                { "key1", "value1" },
                { "key2", null }
            };
            var serializedData = SerializationHelper.Serialize(stateData, SerializationOption.User);

            var deserializedStateData = SerializationHelper.Deserialize<Dictionary<string, string>>(serializedData);

            Assert.NotNull(deserializedStateData);
            Assert.Equal(2, deserializedStateData.Count);

            Assert.Equal("value1", deserializedStateData["key1"]);
            Assert.Equal(null, deserializedStateData["key2"]);
        }

        [Fact, CleanSerializerSettings]
        public void HandlesChangingProcessOfInvocationDataSerialization()
        {
            GlobalConfiguration.Configuration.UseSerializerSettings(SerializerSettingsHelper.DangerousSettings);

            var initialJob = Job.FromExpression(() => Console.WriteLine());
            var invocationData = InvocationData.Serialize(initialJob);

            var serializedInvocationData = SerializationHelper.Serialize(invocationData, SerializationOption.User);

            var deserializedStateData = SerializationHelper.Deserialize<InvocationData>(serializedInvocationData);
            var deserializedJob = deserializedStateData.Deserialize();

            Assert.Equal(initialJob.Args, deserializedJob.Args);
            Assert.Equal(initialJob.Method, deserializedJob.Method);
            Assert.Equal(initialJob.Type, deserializedJob.Type);
        }

        private void UseConnections(Action<SqlConnection, SqlServerConnection> action, bool useBatching = false)
        {
            using (var sqlConnection = ConnectionUtils.CreateConnection())
            {
                var storage = new SqlServerStorage(sqlConnection, new SqlServerStorageOptions { CommandBatchMaxTimeout = useBatching ? TimeSpan.FromMinutes(1) : (TimeSpan?)null });
                using (var connection = new SqlServerConnection(storage))
                {
                    action(sqlConnection, connection);
                }
            }
        }

        private void UseConnection(Action<SqlServerConnection> action)
        {
            using (var sql = ConnectionUtils.CreateConnection())
            {
                var storage = new Mock<SqlServerStorage>(sql);
                storage.Setup(x => x.QueueProviders).Returns(_providers);

                using (var connection = new SqlServerConnection(storage.Object))
                {
                    action(connection);
                }
            }
        }

        public static void SampleMethod(string arg){ }
    }
}
