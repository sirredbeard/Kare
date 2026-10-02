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

/// <summary>Compile time validator for <see cref="OnnxGenAiOptions"/>.</summary>
[OptionsValidator]
public sealed partial class OnnxGenAiOptionsValidator : IValidateOptions<OnnxGenAiOptions>;
