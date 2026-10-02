#include <exception>
#include <string>

#include "ort_genai_c.h"

#if defined(_WIN32)
#define KARE_EXPORT extern "C" __declspec(dllexport)
#else
#define KARE_EXPORT extern "C" __attribute__((visibility("default")))
#endif

KARE_EXPORT const char* KareRegisterExecutionProviderLibrary(
    const char* registration_name,
    const char* library_path) noexcept {
  static thread_local std::string error;

  try {
    OgaRegisterExecutionProviderLibrary(registration_name, library_path);
    error.clear();
    return nullptr;
  } catch (const std::exception& exception) {
    error = exception.what();
  } catch (...) {
    error = "Unknown native exception while registering the execution provider library.";
  }

  return error.c_str();
}
