namespace Mega.Api.Messages
{
	using Newtonsoft.Json;

	/// <summary>
	/// Result is Account is the account is known to Mega, otherwise SuccessResult.
	/// </summary>
	public sealed class SetContactStatusCommand
	{
		[JsonProperty("a")]
		public readonly string CommandName = "ur";

		[JsonProperty("i")]
		public OpaqueID ClientInstanceID { get; set; }

		/// <summary>
		/// See KnownContactStatuses.
		/// </summary>
		[JsonProperty("l")]
		public int Status { get; set; }

		/// <summary>
		/// ID if you know it, email otherwise.
		/// </summary>
		[JsonProperty("u")]
		public string AccountIDOrEmail { get; set; }
	}
}