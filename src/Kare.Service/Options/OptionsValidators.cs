using Kare.Core.Options;
using Kare.Inference.OnnxGenAI;
using Microsoft.Extensions.Options;

namespace Kare.Service.Options;

// ValidateDataAnnotations walks the options type by reflection, which the trim and AOT
// analyzers reject. The [OptionsValidator] source generator produces the same checks at
// compile time, so the limits are still enforced and Native AOT stays viable.

/// <summary>Compile time validator for <see cref="KareServiceOptions"/>.</summary>
[OptionsValidator]
public sealed partial class KareServiceOptionsValidator : IValidateOptions<KareServiceOptions>;

/// <summary>Compile time validator for <see cref="InferenceLimits"/>.</summary>
[OptionsValidator]
public sealed partial class InferenceLimitsValidator : IValidateOptions<InferenceLimits>;

/// <summary>Compile time validator for <see cref="RoutePolicyOptions"/>.</summary>
[OptionsValidator]
public sealed partial class RoutePolicyOptionsValidator : IValidateOptions<RoutePolicyOptions>;

/// <summary>Validates relationships that data annotations cannot express.</summary>
public sealed class RoutePolicySemanticValidator : IValidateOptions<RoutePolicyOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, RoutePolicyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.ModeratePromptCharacterThreshold >= options.ComplexPromptCharacterThreshold)
        {
            return ValidateOptionsResult.Fail(
                "ModeratePromptCharacterThreshold must be lower than ComplexPromptCharacterThreshold.");
        }

        var wireModels = new[]
        {
            options.LocalModelId,
            options.LightModelId,
            options.CloudModelId,
            options.ComplexModelId,
            options.AutomaticModelId,
        };
        if (wireModels.Distinct(StringComparer.Ordinal).Count() != wireModels.Length)
        {
            return ValidateOptionsResult.Fail("Kare routing wire model identifiers must be unique.");
        }

        return ValidateOptionsResult.Success;
    }
}

/// <summary>Compile time validator for <see cref="ResponseCacheOptions"/>.</summary>
[OptionsValidator]
public sealed partial class ResponseCacheOptionsValidator : IValidateOptions<ResponseCacheOptions>;

/// <summary>Compile time validator for <see cref="OnnxGenAiOptions"/>.</summary>
[OptionsValidator]
public sealed partial class OnnxGenAiOptionsValidator : IValidateOptions<OnnxGenAiOptions>;
