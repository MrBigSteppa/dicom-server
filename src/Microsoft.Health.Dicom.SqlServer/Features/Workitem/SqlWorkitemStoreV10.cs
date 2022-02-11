﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using FellowOakDicom;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Health.Dicom.Core;
using Microsoft.Health.Dicom.Core.Exceptions;
using Microsoft.Health.Dicom.Core.Extensions;
using Microsoft.Health.Dicom.Core.Features.ExtendedQueryTag;
using Microsoft.Health.Dicom.Core.Features.Workitem;
using Microsoft.Health.Dicom.Core.Features.Workitem.Model;
using Microsoft.Health.Dicom.Core.Models;
using Microsoft.Health.Dicom.SqlServer.Features.ExtendedQueryTag;
using Microsoft.Health.Dicom.SqlServer.Features.Schema;
using Microsoft.Health.Dicom.SqlServer.Features.Schema.Model;
using Microsoft.Health.SqlServer.Features.Client;
using Microsoft.Health.SqlServer.Features.Storage;

namespace Microsoft.Health.Dicom.SqlServer.Features.Workitem
{
    internal class SqlWorkitemStoreV10 : SqlWorkitemStoreV9
    {
        protected static readonly Health.SqlServer.Features.Schema.Model.NVarCharColumn ProcedureStepStateColumn =
            new Health.SqlServer.Features.Schema.Model.NVarCharColumn("ProcedureStepState", 64);

        protected static readonly Health.SqlServer.Features.Schema.Model.BigIntColumn ProposedWatermarkColumn =
            new Health.SqlServer.Features.Schema.Model.BigIntColumn("ProposedWatermark");

        public SqlWorkitemStoreV10(SqlConnectionWrapperFactory sqlConnectionWrapperFactory, ILogger<ISqlWorkitemStore> logger)
            : base(sqlConnectionWrapperFactory, logger)
        {
        }

        public override SchemaVersion Version => SchemaVersion.V10;

        public override async Task<(long WorkitemKey, long Watermark)?> BeginAddWorkitemWithWatermarkAsync(int partitionKey, DicomDataset dataset, IEnumerable<QueryTag> queryTags, CancellationToken cancellationToken)
        {
            using (SqlConnectionWrapper sqlConnectionWrapper = await SqlConnectionWrapperFactory.ObtainSqlConnectionWrapperAsync(cancellationToken))
            using (SqlCommandWrapper sqlCommandWrapper = sqlConnectionWrapper.CreateSqlCommand())
            {
                var rows = ExtendedQueryTagDataRowsBuilder.Build(dataset, queryTags, Version);
                var parameters = new VLatest.AddWorkitemV10TableValuedParameters(
                    rows.StringRows,
                    rows.DateTimeWithUtcRows,
                    rows.PersonNameRows
                );

                string workitemUid = dataset.GetString(DicomTag.SOPInstanceUID);

                VLatest.AddWorkitemV10.PopulateCommand(
                    sqlCommandWrapper,
                    partitionKey,
                    workitemUid,
                    (byte)IndexStatus.Creating,
                    parameters);

                try
                {
                    using (var reader = await sqlCommandWrapper.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken))
                    {
                        while (await reader.ReadAsync(cancellationToken))
                        {
                            return reader.ReadRow(VLatest.Workitem.WorkitemKey, VLatest.Workitem.Watermark);
                        }
                    }

                    throw new DataStoreException(DicomCoreResource.DataStoreOperationFailed);
                }
                catch (SqlException ex)
                {
                    if (ex.Number == SqlErrorCodes.Conflict)
                    {
                        throw new WorkitemAlreadyExistsException(workitemUid);
                    }

                    throw new DataStoreException(ex);
                }
            }
        }

        public override async Task<WorkitemMetadataStoreEntry> GetWorkitemMetadataAsync(
            int partitionKey, string workitemUid, CancellationToken cancellationToken = default)
        {
            using (SqlConnectionWrapper sqlConnectionWrapper = await SqlConnectionWrapperFactory.ObtainSqlConnectionWrapperAsync(cancellationToken))
            using (SqlCommandWrapper sqlCommandWrapper = sqlConnectionWrapper.CreateSqlCommand())
            {
                var procedureStepStateTagPath = DicomTag.ProcedureStepState.GetPath();

                VLatest.GetWorkitemMetadata
                    .PopulateCommand(sqlCommandWrapper, partitionKey, workitemUid, procedureStepStateTagPath);

                using (var reader = await sqlCommandWrapper.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken))
                {
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        (
                            string wiUid,
                            long workitemKey,
                            int pkey,
                            byte status,
                            string transactionUid,
                            long watermark,
                            string procedureStepState
                        ) = reader.ReadRow(
                                VLatest.Workitem.WorkitemUid,
                                VLatest.Workitem.WorkitemKey,
                                VLatest.Workitem.PartitionKey,
                                VLatest.Workitem.Status,
                                VLatest.Workitem.TransactionUid,
                                VLatest.Workitem.Watermark,
                                ProcedureStepStateColumn);

                        return new WorkitemMetadataStoreEntry(wiUid, workitemKey, watermark, pkey)
                        {
                            Status = (WorkitemStoreStatus)status,
                            TransactionUid = transactionUid,
                            ProcedureStepState = ProcedureStepStateExtensions.GetProcedureStepState(procedureStepState)
                        };
                    }
                }
            }

            return null;
        }

        public override async Task<(long CurrentWatermark, long NextWatermark)?> GetCurrentAndNextWorkitemWatermarkAsync(
            int partitionKey, string workitemUid, CancellationToken cancellationToken = default)
        {
            using (SqlConnectionWrapper sqlConnectionWrapper = await SqlConnectionWrapperFactory.ObtainSqlConnectionWrapperAsync(cancellationToken))
            using (SqlCommandWrapper sqlCommandWrapper = sqlConnectionWrapper.CreateSqlCommand())
            {
                var procedureStepStateTagPath = DicomTag.ProcedureStepState.GetPath();

                VLatest.GetCurrentAndNextWorkitemWatermark
                    .PopulateCommand(sqlCommandWrapper, partitionKey, workitemUid);

                using (var reader = await sqlCommandWrapper.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken))
                {
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        (
                            long watermark,
                            long proposedWatermark
                        ) = reader.ReadRow(
                                VLatest.Workitem.Watermark,
                                ProposedWatermarkColumn);

                        return (watermark, proposedWatermark);
                    }
                }
            }

            return null;
        }

        public override async Task UpdateWorkitemProcedureStepStateAsync(
            WorkitemMetadataStoreEntry workitemMetadata,
            long proposedWatermark,
            string procedureStepState,
            CancellationToken cancellationToken = default)
        {
            using (SqlConnectionWrapper sqlConnectionWrapper = await SqlConnectionWrapperFactory.ObtainSqlConnectionWrapperAsync(cancellationToken))
            using (SqlCommandWrapper sqlCommandWrapper = sqlConnectionWrapper.CreateSqlCommand())
            {
                VLatest.UpdateWorkitemProcedureStepState.PopulateCommand(
                    sqlCommandWrapper,
                    workitemMetadata.WorkitemKey,
                    DicomTag.ProcedureStepState.GetPath(),
                    procedureStepState,
                    workitemMetadata.Watermark,
                    proposedWatermark);

                try
                {
                    await sqlCommandWrapper.ExecuteNonQueryAsync(cancellationToken);
                }
                catch (SqlException ex)
                {
                    throw new DataStoreException(ex);
                }
            }
        }
    }
}