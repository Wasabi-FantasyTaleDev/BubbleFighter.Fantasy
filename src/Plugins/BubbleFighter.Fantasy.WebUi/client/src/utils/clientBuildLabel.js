/** Formats a client release label without confusing a resource patch with its protocol version. */
export function clientBuildLabel(build) {
  const region = build.region.toUpperCase()
  const version = build.patchVersion ? `Patch V${build.patchVersion}` : `V${build.releaseVersion || build.clientVersionMajor || build.id.split('-').at(-1)}`
  return `${region} ${version} (${build.id})`
}
