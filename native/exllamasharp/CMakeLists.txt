cmake_minimum_required(VERSION 3.25)

project(exllamasharp
    VERSION 0.1.0
    LANGUAGES CXX
    DESCRIPTION "Native ExLlamaSharp inference engine (Windows / CUDA)"
)

option(EXL_STUB "Build without CUDA/LibTorch (CI / mock mode)" OFF)
option(EXL_BUILD_TESTS "Build native smoke tests" OFF)
option(EXL_LINK_EXLLAMAV3 "Compile ExLlamaV3 CUDA kernels from third_party (no pybind11)" ON)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
set(CMAKE_EXPORT_COMPILE_COMMANDS ON)

# ---------------------------------------------------------------------------
# Paths
# ---------------------------------------------------------------------------
set(EXLLAMAV3_ROOT "${CMAKE_CURRENT_SOURCE_DIR}/../../third_party/exllamav3"
    CACHE PATH "Path to local ExLlamaV3 tree (do NOT push to upstream)")
set(EXLLAMAV3_EXT_DIR "${EXLLAMAV3_ROOT}/exllamav3/exllamav3_ext")

# ---------------------------------------------------------------------------
# Core sources
# ---------------------------------------------------------------------------
set(EXL_SOURCES
    src/abi.cpp
    src/engine.cpp
    src/pagetable.cpp
    src/scheduler.cpp
    src/kernel_bridge.cpp
)

add_library(exllamasharp SHARED ${EXL_SOURCES})

target_include_directories(exllamasharp
    PUBLIC
        $<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/include>
        $<INSTALL_INTERFACE:include>
    PRIVATE
        ${CMAKE_CURRENT_SOURCE_DIR}/src
)

target_compile_definitions(exllamasharp
    PRIVATE
        EXL_BUILDING_DLL
        $<$<BOOL:${EXL_STUB}>:EXL_STUB>
)

if(MSVC)
    target_compile_options(exllamasharp PRIVATE /W4 /permissive- /Zc:__cplusplus /EHsc /MP)
    target_compile_definitions(exllamasharp PRIVATE NOMINMAX WIN32_LEAN_AND_MEAN)
    set_target_properties(exllamasharp PROPERTIES
        WINDOWS_EXPORT_ALL_SYMBOLS OFF
        OUTPUT_NAME "exllamasharp"
        RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
        LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
        ARCHIVE_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/lib"
    )
else()
    target_compile_options(exllamasharp PRIVATE -Wall -Wextra -Wpedantic)
endif()

# ---------------------------------------------------------------------------
# CUDA + LibTorch + ExLlamaV3 kernels (non-stub)
# ---------------------------------------------------------------------------
if(NOT EXL_STUB)
    enable_language(CUDA)
    set(CMAKE_CUDA_STANDARD 17)
    set(CMAKE_CUDA_STANDARD_REQUIRED ON)

    if(NOT DEFINED CMAKE_CUDA_ARCHITECTURES OR CMAKE_CUDA_ARCHITECTURES STREQUAL "")
        set(CMAKE_CUDA_ARCHITECTURES "80;86;89;90" CACHE STRING "CUDA arch list" FORCE)
    endif()

    find_package(CUDAToolkit 12.0 REQUIRED)
    find_package(Torch REQUIRED)

    target_compile_definitions(exllamasharp PRIVATE EXL_HAS_TORCH)

    if(EXL_LINK_EXLLAMAV3 AND EXISTS "${EXLLAMAV3_EXT_DIR}")
        message(STATUS "ExLlamaV3 ext found at ${EXLLAMAV3_EXT_DIR}")

        # Collect all .c/.cpp/.cu under exllamav3_ext EXCEPT pybind bindings.
        file(GLOB_RECURSE EXL3_EXT_SOURCES CONFIGURE_DEPENDS
            "${EXLLAMAV3_EXT_DIR}/*.c"
            "${EXLLAMAV3_EXT_DIR}/*.cpp"
            "${EXLLAMAV3_EXT_DIR}/*.cu"
        )
        list(FILTER EXL3_EXT_SOURCES EXCLUDE REGEX ".*/bindings\\.cpp$")

        # Object library so we can share includes/flags without pybind11.
        add_library(exllamav3_kernels OBJECT ${EXL3_EXT_SOURCES})
        set_target_properties(exllamav3_kernels PROPERTIES
            CUDA_SEPARABLE_COMPILATION ON
            POSITION_INDEPENDENT_CODE ON
        )
        target_include_directories(exllamav3_kernels PRIVATE
            ${EXLLAMAV3_EXT_DIR}
            ${TORCH_INCLUDE_DIRS}
            ${CUDAToolkit_INCLUDE_DIRS}
        )
        target_compile_definitions(exllamav3_kernels PRIVATE
            NOMINMAX
            WIN32_LEAN_AND_MEAN
            EXL_NO_PYBIND=1
        )
        target_compile_options(exllamav3_kernels PRIVATE
            $<$<COMPILE_LANGUAGE:CXX>:/DNOMINMAX /DWIN32_LEAN_AND_MEAN>
            $<$<COMPILE_LANGUAGE:CUDA>:-DNOMINMAX -DWIN32_LEAN_AND_MEAN -lineinfo -O3 --use_fast_math>
        )
        if(MSVC)
            target_compile_options(exllamav3_kernels PRIVATE
                $<$<COMPILE_LANGUAGE:CXX>:/Ox /Zc:preprocessor /EHsc>
                $<$<COMPILE_LANGUAGE:CUDA>:-Xcompiler=/Zc:preprocessor>
            )
        endif()
        target_link_libraries(exllamav3_kernels PUBLIC
            CUDA::cudart
            CUDA::cublas
            ${TORCH_LIBRARIES}
        )

        target_sources(exllamasharp PRIVATE $<TARGET_OBJECTS:exllamav3_kernels>)
        target_include_directories(exllamasharp PRIVATE ${EXLLAMAV3_EXT_DIR})
        target_compile_definitions(exllamasharp PRIVATE EXL_HAS_EXLLAMAV3_KERNELS)
        target_link_libraries(exllamasharp PRIVATE CUDA::cublas)
        message(STATUS "Linking ExLlamaV3 CUDA kernels (no pybind11)")
    elseif(EXL_LINK_EXLLAMAV3)
        message(WARNING
            "EXL_LINK_EXLLAMAV3=ON but ext dir missing: ${EXLLAMAV3_EXT_DIR}. "
            "Place local ExLlamaV3 under third_party/exllamav3 (do not push upstream).")
    else()
        message(STATUS
            "EXL_LINK_EXLLAMAV3=OFF — native DLL validates EXL3 dirs; generation uses Python worker.")
    endif()

    target_link_libraries(exllamasharp PRIVATE CUDA::cudart ${TORCH_LIBRARIES})
    target_include_directories(exllamasharp PRIVATE ${TORCH_INCLUDE_DIRS})

    if(MSVC)
        set_property(TARGET exllamasharp PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreadedDLL")
    endif()

    message(STATUS "Building exllamasharp WITH CUDA + LibTorch")
else()
    message(STATUS "Building exllamasharp in EXL_STUB mode (no CUDA)")
endif()

find_package(Threads REQUIRED)
target_link_libraries(exllamasharp PRIVATE Threads::Threads)

install(TARGETS exllamasharp
    RUNTIME DESTINATION bin
    LIBRARY DESTINATION lib
    ARCHIVE DESTINATION lib
)
install(FILES include/exllamasharp.h DESTINATION include)

message(STATUS "exllamasharp ${PROJECT_VERSION}")
message(STATUS "  EXL_STUB=${EXL_STUB}")
message(STATUS "  EXL_LINK_EXLLAMAV3=${EXL_LINK_EXLLAMAV3}")
message(STATUS "  EXLLAMAV3_ROOT=${EXLLAMAV3_ROOT}")
