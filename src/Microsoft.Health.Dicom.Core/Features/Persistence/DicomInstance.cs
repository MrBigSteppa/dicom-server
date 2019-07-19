﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using Dicom;
using EnsureThat;
using Microsoft.Health.Dicom.Core.Features.Validation;
using Newtonsoft.Json;

namespace Microsoft.Health.Dicom.Core.Features.Persistence
{
    public class DicomInstance : IDicomResource
    {
        [JsonConstructor]
        public DicomInstance(string studyInstanceUID, string seriesInstanceUID, string sopInstanceUID)
        {
            // Run the instance identifiers through the regular expression check.
            EnsureArg.IsTrue(DicomIdentifierValidator.IdentifierRegex.IsMatch(studyInstanceUID), nameof(studyInstanceUID));
            EnsureArg.IsTrue(DicomIdentifierValidator.IdentifierRegex.IsMatch(seriesInstanceUID), nameof(seriesInstanceUID));
            EnsureArg.IsTrue(DicomIdentifierValidator.IdentifierRegex.IsMatch(sopInstanceUID), nameof(sopInstanceUID));
            EnsureArg.IsNotEqualTo(studyInstanceUID, seriesInstanceUID, nameof(seriesInstanceUID));
            EnsureArg.IsNotEqualTo(sopInstanceUID, studyInstanceUID, nameof(sopInstanceUID));
            EnsureArg.IsNotEqualTo(sopInstanceUID, seriesInstanceUID, nameof(sopInstanceUID));

            StudyInstanceUID = studyInstanceUID;
            SeriesInstanceUID = seriesInstanceUID;
            SopInstanceUID = sopInstanceUID;
        }

        public string StudyInstanceUID { get; }

        public string SeriesInstanceUID { get; }

        public string SopInstanceUID { get; }

        public static DicomInstance Create(DicomDataset dicomDataset)
        {
            EnsureArg.IsNotNull(dicomDataset, nameof(dicomDataset));

            // Note: Here we 'GetSingleValueOrDefault' and let the constructor validate the identifier.
            return new DicomInstance(
                dicomDataset.GetSingleValueOrDefault(DicomTag.StudyInstanceUID, string.Empty),
                dicomDataset.GetSingleValueOrDefault(DicomTag.SeriesInstanceUID, string.Empty),
                dicomDataset.GetSingleValueOrDefault(DicomTag.SOPInstanceUID, string.Empty));
        }

        public override bool Equals(object obj)
        {
            if (obj is DicomInstance identity)
            {
                return StudyInstanceUID.Equals(identity.StudyInstanceUID, DicomStudy.EqualsStringComparison) &&
                        SeriesInstanceUID.Equals(identity.SeriesInstanceUID, DicomStudy.EqualsStringComparison) &&
                        SopInstanceUID.Equals(identity.SopInstanceUID, DicomStudy.EqualsStringComparison);
            }

            return false;
        }

        public override int GetHashCode()
            => (StudyInstanceUID + SeriesInstanceUID + SopInstanceUID).GetHashCode(DicomStudy.EqualsStringComparison);

        public override string ToString()
            => $"Study Instance UID: {StudyInstanceUID}, Series Instance UID: {SeriesInstanceUID}, SOP Instance UID {SopInstanceUID}";
    }
}
