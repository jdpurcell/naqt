using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace naqt;

public static class Network {
	public static readonly byte[] DummySha256Hash = new byte[64];

	private static readonly HttpClient Client = new();

	public static async Task<byte[]> GetPublishedSha256ForFileAsync(string url, CancellationToken cancellationToken = default) {
		string content = await GetAsStringAsync(url + ".sha256", cancellationToken);
		if (content.Length < 65 || content[64] != ' ') {
			throw new Exception("Published hash is formatted incorrectly.");
		}
		return Convert.FromHexString(content[0..64]);
	}

	public static async Task<string> GetAsStringAsync(string url, CancellationToken cancellationToken = default) {
		using HttpResponseMessage response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
		return await response.EnsureSuccessStatusCode().Content.ReadAsStringAsync(cancellationToken);
	}

	public static async Task GetToStreamAsync(string url, Stream dest, CancellationToken cancellationToken = default) {
		using HttpResponseMessage response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
		await response.EnsureSuccessStatusCode().Content.CopyToAsync(dest, cancellationToken);
	}

	public static async Task GetToStreamWithSha256ValidationAsync(string url, Stream dest, byte[] expectedHash, CancellationToken cancellationToken = default) {
		using HashAlgorithm hashAlgo = SHA256.Create();
		await using (CryptoStream cryptoStream = new(dest, hashAlgo, CryptoStreamMode.Write, true)) {
			await GetToStreamAsync(url, cryptoStream, cancellationToken);
		}
		if (!ReferenceEquals(expectedHash, DummySha256Hash) && !hashAlgo.Hash!.SequenceEqual(expectedHash)) {
			throw new Exception("Hash of downloaded file is incorrect.");
		}
	}

	public static async Task<string> GetAsUtf8StringWithSha256ValidationAsync(string url, byte[] expectedHash, CancellationToken cancellationToken = default) {
		using MemoryStream memoryStream = new();
		await GetToStreamWithSha256ValidationAsync(url, memoryStream, expectedHash, cancellationToken);
		return Encoding.UTF8.GetString(memoryStream.GetBuffer(), 0, (int)memoryStream.Length);
	}
}
