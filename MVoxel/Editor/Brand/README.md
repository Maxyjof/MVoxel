# MVoxel brand assets

The MVoxel mark is an isometric M assembled from cubic voxels, using fresh sky
blue and coral red with a transparent background. `MVoxelLogo.png` serves the welcome screen and runtime window icon;
`MVoxelLogo.ico` is embedded into the Windows executable. The vector master is
`MVoxelLogoGrid.svg`.

All three files are generated from the same grid and palette with
`python tools/generate_brand.py` at the repository root. The ICO contains
16/24/32/48/64/128/256-pixel PNG layers. Keep the icon square and use nearest-
neighbor resizing so the voxel edges stay crisp. The standard-library generator
uses a shared isometric projection for all cube faces and requires no image
packages.
