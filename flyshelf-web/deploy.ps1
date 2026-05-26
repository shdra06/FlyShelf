# FlyShelf Website Deploy Script
# Builds the Next.js site and pushes to gh-pages branch of the public repo
# Usage: .\deploy.ps1

Write-Host "Building Next.js site..." -ForegroundColor Cyan
npm run build
if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed!" -ForegroundColor Red
    exit 1
}

Write-Host "Adding .nojekyll..." -ForegroundColor Cyan
New-Item -Path "out\.nojekyll" -ItemType File -Force | Out-Null

Write-Host "Deploying to gh-pages..." -ForegroundColor Cyan
Push-Location out

# Initialize temporary git repo
git init
git checkout -b gh-pages
git add -A
git commit -m "Deploy FlyShelf website"
git remote add origin https://github.com/shdra06/FlyShelf.git
git push -f origin gh-pages

# Cleanup
Pop-Location
Remove-Item -Recurse -Force "out\.git"

Write-Host "Deployed successfully. GitHub Pages takes 1-3 minutes to update." -ForegroundColor Cyan
