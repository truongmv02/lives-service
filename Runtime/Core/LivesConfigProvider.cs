namespace TMV.Lives
{
    /// <summary>
    /// Base config provider. Override Get() to source config from a ScriptableObject,
    /// remote config system, or A/B test provider without changing LivesService.
    /// </summary>
    public class LivesConfigProvider
    {
        /// <summary>Returns the active LivesConfig. Called once inside Initialize().</summary>
        public virtual LivesConfig Get() => new LivesConfig();
    }
}
