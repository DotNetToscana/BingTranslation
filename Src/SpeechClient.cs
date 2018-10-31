﻿using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using TranslatorService;
using TranslatorService.Models;

namespace TranslatorService
{
    /// <summary>
    /// The <strong>SpeechClient</strong> class provides methods for text-to-speech and speech-to-text
    /// </summary>
    /// <remarks>
    /// <para>To use this class, you must register Speech Sercvice on https://portal.azure.com/#create/Microsoft.CognitiveServicesSpeechServices to obtain the Subscription key.
    /// </para>
    /// </remarks>
    public class SpeechClient : ISpeechClient
    {
        private const string BaseAuthorizationUri = "https://{0}.api.cognitive.microsoft.com/sts/v1.0/issueToken";
        private const string BaseTextToSpeechRequestUri = "https://{0}.tts.speech.microsoft.com/cognitiveservices/v1";
        private const string BaseSpeechToTextRequestUri = "https://{0}.stt.speech.microsoft.com/speech/recognition/conversation/cognitiveservices/v1";
        private const string AuthorizationHeader = "Authorization";

        private const string JsonMediaType = "application/json";
        private const string WavAudioMediaType = "audio/wav";

        private const int BufferSize = 1024;
        private const int MaxTextLengthForSpeech = 800;

        private static HttpClient client;
        private static HttpClientHandler handler;
        private static SpeechClient instance;

        /// <summary>
        /// Gets public singleton property.
        /// </summary>
        public static SpeechClient Instance => instance ?? (instance = new SpeechClient());

        private AzureAuthToken authToken;
        private string authorizationHeaderValue;

        /// <summary>
        /// Initializes a new instance of the <see cref="SpeechClient"/> class.
        /// </summary>
        /// <param name="region">The Azure region of the the Speech service.</param>
        /// <param name="subscriptionKey">The Subscription Key to use the service (it must be created in the specified <paramref name="region"/>).</param>
        /// <remarks>
        /// <para>You must register Speech Service on https://portal.azure.com/#create/Microsoft.CognitiveServicesSpeechServices to obtain the Speech Uri, Authentication Uri and Subscription key needed to use the service.</para>
        /// </remarks>
        public SpeechClient(string region = null, string subscriptionKey = null)
        {
            var cookieContainer = new CookieContainer();
            handler = new HttpClientHandler { CookieContainer = new CookieContainer(), UseProxy = false };
            client = new HttpClient(handler);

            authToken = new AzureAuthToken(subscriptionKey, string.Format(BaseAuthorizationUri, region));
            TextToSpeechRequestUri = string.Format(BaseTextToSpeechRequestUri, region);
            SpeechToTextRequestUri = string.Format(BaseSpeechToTextRequestUri, region);
        }

        /// <inheritdoc/>
        public string SubscriptionKey
        {
            get => authToken.SubscriptionKey;
            set => authToken.SubscriptionKey = value;
        }

        /// <inheritdoc/>
        public string AuthenticationUri
        {
            get => authToken.ServiceUrl.ToString();
            set => authToken.ServiceUrl = new Uri(value);
        }

        /// <inheritdoc/>
        public string TextToSpeechRequestUri { get; set; }

        /// <inheritdoc/>
        public string SpeechToTextRequestUri { get; set; }

        /// <inheritdoc/>
        public async Task<Stream> SpeakAsync(TextToSpeechParameters input)
        {
            if (input == null)
            {
                throw new ArgumentNullException(nameof(input));
            }

            if (input.Text?.Length > MaxTextLengthForSpeech)
            {
                throw new ArgumentException($"Input text cannot be null or longer than {MaxTextLengthForSpeech} characters");
            }

            client.DefaultRequestHeaders.Clear();
            foreach (var header in input.Headers)
            {
                client.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
            }

            var genderValue = input.VoiceType == Gender.Male ? "Male" : "Female";
            var request = new HttpRequestMessage(HttpMethod.Post, TextToSpeechRequestUri)
            {
                Content = new StringContent(GenerateSsml(input.Language, genderValue, input.VoiceName, input.Text))
            };

            // Checks if it is necessary to obtain/update access token.
            await CheckUpdateTokenAsync().ConfigureAwait(false);
            request.Headers.Add(AuthorizationHeader, authorizationHeaderValue);

            var responseMessage = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);

            try
            {
                if (responseMessage.IsSuccessStatusCode)
                {
                    var httpStream = await responseMessage.Content.ReadAsStreamAsync().ConfigureAwait(false);
                    var result = new MemoryStream();
                    await httpStream.CopyToAsync(result);
                    result.Position = 0;

                    return result;
                }
                else
                {
                    throw new ServiceException(responseMessage.ReasonPhrase, (int)responseMessage.StatusCode);
                }
            }
            catch (Exception ex)
            {
                throw new ServiceException(ex.GetBaseException().Message, 500);
            }
            finally
            {
                responseMessage.Dispose();
                request.Dispose();
            }
        }

        /// <inheritdoc/>
        public async Task<SpeechRecognitionResponse> RecognizeAsync(Stream audioStream, string language, ProfanityMode profanity = ProfanityMode.Masked)
        {
            // Checks if it is necessary to obtain/update access token.
            await CheckUpdateTokenAsync().ConfigureAwait(false);

            var requestUri = $"{SpeechToTextRequestUri}?language={language}&profanity={profanity}";
            var request = new HttpRequestMessage(HttpMethod.Post, requestUri);

            request.Headers.TransferEncodingChunked = true;
            request.Headers.ExpectContinue = true;
            request.Headers.Accept.ParseAdd(JsonMediaType);
            request.Headers.Host = request.RequestUri.Host;
            request.Headers.Add(AuthorizationHeader, authorizationHeaderValue);

            request.Content = PopulateSpeechToTextRequestContent(audioStream);
            var response = await client.SendAsync(request).ConfigureAwait(false);

            var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Continue)
            {
                //if we get a valid response (non-null, no exception, and not forbidden), return the response
                return JsonConvert.DeserializeObject<SpeechRecognitionResponse>(content);
            }
            else
            {
                var error = JToken.Parse(content);
                throw new ServiceException(error["Message"].ToString(), (int)response.StatusCode);
            }
        }

        /// <inheritdoc/>
        public Task InitializeAsync() => CheckUpdateTokenAsync();

        /// <inheritdoc/>
        public Task InitializeAsync(string region, string subscriptionKey)
        {
            authToken = new AzureAuthToken(subscriptionKey, string.Format(BaseAuthorizationUri, region));
            TextToSpeechRequestUri = string.Format(BaseTextToSpeechRequestUri, region);
            SpeechToTextRequestUri = string.Format(BaseSpeechToTextRequestUri, region);

            return InitializeAsync();
        }

        private async Task CheckUpdateTokenAsync()
        {
            // If necessary, updates the access token.
            authorizationHeaderValue = await authToken.GetAccessTokenAsync().ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            client.Dispose();
            handler.Dispose();
        }

        private string GenerateSsml(string locale, string gender, string name, string text)
        {
            var ssmlDoc = new XDocument(
                              new XElement("speak",
                                  new XAttribute("version", "1.0"),
                                  new XAttribute(XNamespace.Xml + "lang", "en-US"),
                                  new XElement("voice",
                                      new XAttribute(XNamespace.Xml + "lang", locale),
                                      new XAttribute(XNamespace.Xml + "gender", gender),
                                      new XAttribute("name", name),
                                      text)));
            return ssmlDoc.ToString();
        }

        private HttpContent PopulateSpeechToTextRequestContent(Stream audioStream)
        {
            return new PushStreamContent(async (outputStream, httpContext, transportContext) =>
            {
                byte[] buffer = null;
                var bytesRead = 0;

                using (outputStream) //must close/dispose output stream to notify that content is done
                {
                    //read 1024 (BufferSize) (max) raw bytes from the input audio file
                    buffer = new byte[checked((uint)Math.Min(BufferSize, (int)audioStream.Length))];

                    while ((bytesRead = await audioStream.ReadAsync(buffer, 0, buffer.Length)) != 0)
                    {
                        await outputStream.WriteAsync(buffer, 0, bytesRead);
                    }

                    await outputStream.FlushAsync();
                }
            }, new MediaTypeHeaderValue(WavAudioMediaType));
        }
    }
}