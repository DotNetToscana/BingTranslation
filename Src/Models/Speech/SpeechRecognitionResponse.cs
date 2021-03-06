﻿using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace TranslatorService.Models.Speech
{
    /// <summary>
    /// The <strong>SpeechRecognitionResponse</strong> class contains information about a successful recognition operation.
    /// </summary>
    /// <seealso cref="SpeechClient"/>
    public class SpeechRecognitionResponse
    {
        /// <summary>
        /// A string indicating the result status. Successful requests will return "Success"
        /// </summary>
        public RecognitionStatus RecognitionStatus { get; init; }

        /// <summary>
        /// Gets or sets the offset.
        /// The Offset element specifies the offset (in 100-nanosecond units) at which the phrase was recognized, relative to the start of the audio stream.
        /// </summary>
        public long Offset { get; init; }

        /// <summary>
        /// The duration of speech.
        /// The Duration element specifies the duration (in 100-nanosecond units) of this speech phrase.
        /// </summary>
        public long Duration { get; init; }

        private string? displayText;
        /// <summary>
        /// Gets or sets the top result (by confidence), returned in Display Form.
        /// </summary>
        /// <remarks>The display form adds punctuation and capitalization to recognition results, making it the most appropriate form for applications that display the spoken text.</remarks>
        public string? DisplayText
        {
            get => displayText ?? Alternatives?.FirstOrDefault()?.Display;
            init => displayText = value;
        }

        /// <summary>
        /// A list of alternative interpretations of the same speech recognition result. These results are ranked from most likely to least likely The first entry is the same as the main recognition result.
        /// </summary>
        [JsonPropertyName("NBest")]
        public IEnumerable<RecognitionAlternative> Alternatives { get; init; } = new List<RecognitionAlternative>();
    }
}
