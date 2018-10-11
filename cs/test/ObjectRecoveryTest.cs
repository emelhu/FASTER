﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license.

using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using FASTER.core;
using NUnit.Framework;

namespace FASTER.test.recovery.objectstore
{

    [TestFixture]
    internal class ObjectRecoveryTests
    {
        const long numUniqueKeys = (1 << 14);
        const long keySpace = (1L << 14);
        const long numOps = (1L << 19);
        const long refreshInterval = (1L << 8);
        const long completePendingInterval = (1L << 10);
        const long checkpointInterval = (1L << 16);
        private IManagedFasterKV<AdId, NumClicks, Input, Output, Empty> fht;
        private string test_path;
        private Guid token;
        private IDevice log, objlog;

        [SetUp]
        public void Setup()
        {
            if (test_path == null)
            {
                test_path = Path.GetTempPath() + Path.GetRandomFileName();
                if (!Directory.Exists(test_path))
                    Directory.CreateDirectory(test_path);
            }

            log = FasterFactory.CreateLogDevice(test_path + "\\hlog", deleteOnClose: true);
            objlog = FasterFactory.CreateObjectLogDevice(test_path + "\\hlog", deleteOnClose: true);

            fht = 
                FasterFactory.Create
                <AdId, NumClicks, Input, Output, Empty, Functions>
                (keySpace, log, objlog, checkpointDir: test_path, functions: new Functions());
        }

        [TearDown]
        public void TearDown()
        {
            fht.StopSession();
            fht = null;
            log.Close();
            objlog.Close();
            DeleteDirectory(test_path);
        }

        public static void DeleteDirectory(string path)
        {
            foreach (string directory in Directory.GetDirectories(path))
            {
                DeleteDirectory(directory);
            }

            try
            {
                Directory.Delete(path, true);
            }
            catch (IOException)
            {
                Directory.Delete(path, true);
            }
            catch (UnauthorizedAccessException)
            {
                Directory.Delete(path, true);
            }
        }

        [Test]
        public void ObjectRecoveryTest1()
        {
            Populate();
            Setup();
            RecoverAndTest(token, token);
        }

        public unsafe void Populate()
        {
            // Prepare the dataset
            var inputArray = new Tuple<AdId, Input>[numOps];
            for (int i = 0; i < numOps; i++)
            {
                inputArray[i] = new Tuple<AdId, Input>
                    (new AdId { adId = i % numUniqueKeys }, new Input { numClicks = 1 });
            }

            // Register thread with FASTER
            fht.StartSession();

            // Prpcess the batch of input data
            bool first = true;
            for (int i = 0; i < numOps; i++)
            {
                fht.RMW(inputArray[i].Item1, inputArray[i].Item2, default(Empty), i);

                if ((i + 1) % checkpointInterval == 0)
                {
                    if (first)
                        while (!fht.TakeFullCheckpoint(out token))
                            fht.Refresh();
                    else
                        while (!fht.TakeFullCheckpoint(out Guid nextToken))
                            fht.Refresh();

                    fht.CompleteCheckpoint(true);

                    first = false;
                }

                if (i % completePendingInterval == 0)
                {
                    fht.CompletePending(false);
                }
                else if (i % refreshInterval == 0)
                {
                    fht.Refresh();
                }
            }


            // Make sure operations are completed
            fht.CompletePending(true);

            // Deregister thread from FASTER
            fht.StopSession();
        }

        public unsafe void RecoverAndTest(Guid cprVersion, Guid indexVersion)
        {
            // Recover
            fht.Recover(cprVersion, indexVersion);

            // Create array for reading
            var inputArray = new Tuple<AdId, Input>[numUniqueKeys];
            for (int i = 0; i < numUniqueKeys; i++)
            {
                inputArray[i] = new Tuple<AdId, Input>(new AdId { adId = i }, new Input { numClicks = 0 });
            }

            var outputArray = new Output[numUniqueKeys];
            for (int i = 0; i < numUniqueKeys; i++)
            {
                outputArray[i] = new Output();
            }

            // Register with thread
            fht.StartSession();

            // Issue read requests
            for (var i = 0; i < numUniqueKeys; i++)
            {
                fht.Read(inputArray[i].Item1, null, ref outputArray[i], default(Empty), i);
            }

            // Complete all pending requests
            fht.CompletePending(true);

            // Release
            fht.StopSession();

            // Set checkpoint directory
            Config.CheckpointDirectory = test_path;

            // Test outputs
            var checkpointInfo = default(HybridLogRecoveryInfo);
            checkpointInfo.Recover(cprVersion);

            // Compute expected array
            long[] expected = new long[numUniqueKeys];
            foreach (var guid in checkpointInfo.continueTokens.Keys)
            {
                var sno = checkpointInfo.continueTokens[guid];
                for (long i = 0; i <= sno; i++)
                {
                    var id = i % numUniqueKeys;
                    expected[id]++;
                }
            }

            int threadCount = 1; // single threaded test
            int numCompleted = threadCount - checkpointInfo.continueTokens.Count;
            for (int t = 0; t < numCompleted; t++)
            {
                var sno = numOps;
                for (long i = 0; i < sno; i++)
                {
                    var id = i % numUniqueKeys;
                    expected[id]++;
                }
            }

            // Assert if expected is same as found
            for (long i = 0; i < numUniqueKeys; i++)
            {
                Assert.IsTrue(
                    expected[i] == outputArray[i].numClicks.numClicks,
                    "Debug error for AdId {0}: Expected ({1}), Found({2})", 
                    inputArray[i].Item1.adId,
                    expected[i], 
                    outputArray[i].numClicks.numClicks);
            }
        }
    }
}
