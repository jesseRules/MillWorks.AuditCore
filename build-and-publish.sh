#!/bin/bash

# Build and publish MillWorks.AuditCore packages to a local NuGet repository
# For local development. CI/CD is the primary publish path — see .github/workflows/

# Default values
LOCAL_REPO_PATH="${HOME}/LocalNuGetPackages"
VERSION=""

# Parse arguments
while [[ $# -gt 0 ]]; do
    case $1 in
        -p|--path)
            LOCAL_REPO_PATH="$2"
            shift 2
            ;;
        -v|--version)
            VERSION="$2"
            shift 2
            ;;
        -h|--help)
            echo "Usage: $0 [-p|--path PATH] [-v|--version VERSION]"
            echo ""
            echo "Options:"
            echo "  -p, --path PATH       Local repository path (default: ~/LocalNuGetPackages)"
            echo "  -v, --version VERSION Package version (default: read from Directory.Build.props)"
            echo "  -h, --help           Show this help message"
            exit 0
            ;;
        *)
            echo "Unknown option: $1"
            exit 1
            ;;
    esac
done

# Read version from Directory.Build.props if not specified
if [ -z "$VERSION" ]; then
    VERSION=$(grep -oPm1 '(?<=<Version>)[^<]+' Directory.Build.props 2>/dev/null)
    if [ -z "$VERSION" ]; then
        echo "ERROR: Could not read version from Directory.Build.props and no -v flag provided"
        exit 1
    fi
fi

# Colors
RED='\033[0;31m'
GREEN='\033[0;32m'
CYAN='\033[0;36m'
NC='\033[0m'

echo -e "${CYAN}========================================"
echo "MillWorks.AuditCore Package Builder v${VERSION}"
echo -e "========================================${NC}"
echo ""

# Create local NuGet repository directory if it doesn't exist
if [ ! -d "$LOCAL_REPO_PATH" ]; then
    echo -e "${CYAN}Creating local NuGet repository at: $LOCAL_REPO_PATH${NC}"
    mkdir -p "$LOCAL_REPO_PATH"
fi

# Projects in dependency order (correct after Core/Abstractions merge and dependency inversion)
projects=(
    "src/MillWorks.AuditCore.Abstractions"
    "src/MillWorks.AuditCore.EntityFramework"
    "src/MillWorks.AuditCore.Providers"
    "src/MillWorks.AuditCore.Services"
    "src/MillWorks.AuditCore.AspNetCore"
)

# Clean previous builds
echo -e "${CYAN}Cleaning previous builds...${NC}"
for project in "${projects[@]}"; do
    project_name=$(basename "$project")
    project_path="$PWD/$project"
    if [ -d "$project_path" ]; then
        dotnet clean "$project_path/$project_name.csproj" --configuration Release > /dev/null 2>&1
    fi
done
echo -e "${GREEN}Clean completed!${NC}"
echo ""

# Build and pack each project
packages_created=()
errors=()

for project in "${projects[@]}"; do
    project_name=$(basename "$project")
    echo -e "${CYAN}Processing: $project_name${NC}"
    project_path="$PWD/$project"
    csproj_file="$project_path/$project_name.csproj"

    if [ ! -f "$csproj_file" ]; then
        echo -e "  ${RED}ERROR: Project file not found: $csproj_file${NC}"
        errors+=("$project_name - Project file not found")
        continue
    fi

    # Build the project
    echo -e "  ${CYAN}Building...${NC}"
    build_output=$(dotnet build "$csproj_file" --configuration Release 2>&1)
    if [ $? -ne 0 ]; then
        echo -e "  ${RED}ERROR: Build failed for $project_name${NC}"
        echo "$build_output"
        errors+=("$project_name - Build failed")
        continue
    fi
    echo -e "  ${GREEN}Build succeeded!${NC}"

    # Pack the project
    echo -e "  ${CYAN}Creating NuGet package...${NC}"
    pack_output=$(dotnet pack "$csproj_file" --configuration Release --no-build --output "$LOCAL_REPO_PATH" /p:Version="$VERSION" 2>&1)
    if [ $? -ne 0 ]; then
        echo -e "  ${RED}ERROR: Pack failed for $project_name${NC}"
        echo "$pack_output"
        errors+=("$project_name - Pack failed")
        continue
    fi

    package_file="$project_name.$VERSION.nupkg"
    packages_created+=("$package_file")
    echo -e "  ${GREEN}Package created: $package_file${NC}"
    echo ""
done

# Summary
echo -e "${CYAN}========================================"
echo "Build Summary"
echo -e "========================================${NC}"
echo ""

if [ ${#packages_created[@]} -gt 0 ]; then
    echo -e "${GREEN}Successfully created ${#packages_created[@]} package(s):${NC}"
    for package in "${packages_created[@]}"; do
        echo -e "  ${GREEN}- $package${NC}"
    done
    echo ""
    echo -e "${GREEN}Packages saved to: $LOCAL_REPO_PATH${NC}"
fi

if [ ${#errors[@]} -gt 0 ]; then
    echo ""
    echo -e "${RED}Errors encountered:${NC}"
    for error in "${errors[@]}"; do
        echo -e "  ${RED}- $error${NC}"
    done
fi

echo ""
echo -e "${CYAN}========================================"
echo "Next Steps"
echo -e "========================================${NC}"
echo ""
echo -e "${CYAN}1. Add the local NuGet source to your solution:${NC}"
echo "   dotnet nuget add source $LOCAL_REPO_PATH --name MillWorksLocal"
echo ""
echo -e "${CYAN}2. Add package reference to your project:${NC}"
echo "   dotnet add package MillWorks.AuditCore.AspNetCore --version $VERSION"
echo ""
echo -e "${CYAN}Note: CI/CD is the primary publish path. See .github/workflows/publish.yml${NC}"
echo ""
