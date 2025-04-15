using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace naqt;

public static class Network {
	public static readonly byte[] DummySha256Hash = new byte[64];

	private static readonly HttpClient Client = new();

	private static async Task<string> GetAsStringAsync(string url, CancellationToken cancellationToken = default) {
		using HttpResponseMessage response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
		return await response.EnsureSuccessStatusCode().Content.ReadAsStringAsync(cancellationToken);
	}

	private static async Task GetToStreamAsync(string url, Stream dest, CancellationToken cancellationToken = default) {
		using HttpResponseMessage response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
		await response.EnsureSuccessStatusCode().Content.CopyToAsync(dest, cancellationToken);
	}

	public static async Task<byte[]> GetPublishedSha256ForFileAsync(QtUrl fileUrl, CancellationToken cancellationToken = default) {
		QtUrl hashUrl = new(Constants.TrustedMirror, fileUrl.Path + ".sha256");
		string content = await GetAsStringAsync(hashUrl.ToString(), cancellationToken);
		if (content.Length < 65 || content[64] != ' ') {
			throw new Exception("Published hash is formatted incorrectly.");
		}
		return Convert.FromHexString(content[0..64]);
	}

	public static async Task GetToStreamWithSha256ValidationAsync(QtUrl url, Stream dest, byte[] expectedHash, CancellationToken cancellationToken = default) {
		using HashAlgorithm hashAlgo = SHA256.Create();
		await using (CryptoStream cryptoStream = new(dest, hashAlgo, CryptoStreamMode.Write, true)) {
			await GetToStreamAsync(url.ToString(), cryptoStream, cancellationToken);
		}
		if (!ReferenceEquals(expectedHash, DummySha256Hash) && !hashAlgo.Hash!.SequenceEqual(expectedHash)) {
			throw new Exception("Hash of downloaded file is incorrect.");
		}
	}

	public static async Task<string> GetAsUtf8StringWithSha256ValidationAsync(QtUrl url, byte[] expectedHash, CancellationToken cancellationToken = default) {
		using MemoryStream stream = new();
		await GetToStreamWithSha256ValidationAsync(url, stream, expectedHash, cancellationToken);
		return stream.ReadAllText();
	}
}
