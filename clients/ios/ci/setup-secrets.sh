#!/usr/bin/env bash
# One-time setup, and yearly renewal, of the iOS release secrets. Run by a maintainer. Prints no secret values.
#
#   setup-secrets.sh request <folder>
#       Makes a private key and a certificate request (.csr) to upload at developer.apple.com.
#   setup-secrets.sh install <private.key> <distribution.cer> <profile.mobileprovision> <AuthKey_ID.p8> <issuer-id>
#       Bundles the certificate and stores everything as secrets on the GitHub environments.
set -euo pipefail

repo=TechDaddyKB/cuebot

usage() { sed -n '2,7p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//' >&2; exit 2; }
b64() { base64 < "$1" | tr -d '\n'; }
put() { printf '%s' "$3" | gh secret set "$2" --env "$1" --repo "$repo" > /dev/null; echo "  $1: $2"; }

case "${1:-}" in
  request)
    [ $# -eq 2 ] || usage
    umask 077
    mkdir -p "$2"
    [ ! -e "$2/ios-distribution.key" ] || { echo "$2/ios-distribution.key already exists." >&2; exit 1; }
    openssl genrsa -out "$2/ios-distribution.key" 2048 2> /dev/null
    openssl req -new -key "$2/ios-distribution.key" -out "$2/ios-distribution.csr" -subj "/CN=Cuebot iOS Distribution"
    echo "Upload $2/ios-distribution.csr when creating an Apple Distribution certificate."
    echo "Keep $2/ios-distribution.key private."
    ;;
  install)
    [ $# -eq 6 ] || usage
    key="$2" cer="$3" profile="$4" p8="$5" issuer="$6"
    for file in "$key" "$cer" "$profile" "$p8"; do
      [ -f "$file" ] || { echo "Not found: $file" >&2; exit 1; }
    done
    key_id=$(basename "$p8" .p8)
    key_id=${key_id#AuthKey_}
    [[ "$key_id" =~ ^[A-Z0-9]{10}$ ]] || { echo "Expected the key file to be named AuthKey_<ID>.p8." >&2; exit 1; }
    gh api "repos/$repo/environments/app-store-review" > /dev/null

    work=$(mktemp -d)
    trap 'rm -rf "$work"' EXIT
    chmod 700 "$work"
    openssl x509 -inform DER -in "$cer" -out "$work/cert.pem"
    if [ "$(openssl x509 -in "$work/cert.pem" -noout -pubkey | openssl sha256)" != \
         "$(openssl pkey -in "$key" -pubout | openssl sha256)" ]; then
      echo "The certificate does not belong to this private key." >&2
      exit 1
    fi
    openssl x509 -in "$work/cert.pem" -noout -subject | grep -q "Apple Distribution" || {
      echo "This is not an Apple Distribution certificate." >&2
      exit 1
    }
    CUEBOT_P12_PASSWORD=$(openssl rand -hex 24)
    export CUEBOT_P12_PASSWORD
    # The macOS keychain cannot read OpenSSL 3's default .p12 encryption.
    legacy=()
    if openssl pkcs12 -help 2>&1 | grep -q -- '-legacy'; then legacy=(-legacy); fi
    openssl pkcs12 -export "${legacy[@]}" -inkey "$key" -in "$work/cert.pem" -out "$work/dist.p12" \
      -name "Apple Distribution" -passout env:CUEBOT_P12_PASSWORD

    echo "Storing secrets:"
    put signing IOS_DIST_CERT_P12_BASE64 "$(b64 "$work/dist.p12")"
    put signing IOS_DIST_CERT_PASSWORD "$CUEBOT_P12_PASSWORD"
    put signing IOS_APPSTORE_PROFILE_BASE64 "$(b64 "$profile")"
    for env in signing app-store-review; do
      put "$env" ASC_KEY_ID "$key_id"
      put "$env" ASC_ISSUER_ID "$issuer"
      put "$env" ASC_KEY_P8_BASE64 "$(b64 "$p8")"
    done
    unset CUEBOT_P12_PASSWORD
    echo "Done. Test it: gh workflow run ios-release.yml --repo $repo --ref main -f mode=build-only"
    ;;
  *) usage ;;
esac
