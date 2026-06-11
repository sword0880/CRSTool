"""Custom exceptions for the tax assistant."""


class TaxAssistantError(Exception):
    """Base exception for all tax assistant errors."""
    pass


class ParseException(TaxAssistantError):
    """Raised when Excel parsing fails."""
    pass


class InventoryException(TaxAssistantError):
    """Raised when sell quantity exceeds available inventory."""
    pass


class UnsupportedCurrencyException(TaxAssistantError):
    """Raised when currency is not supported or missing from config."""
    pass


class TaxException(TaxAssistantError):
    """Raised when tax calculation encounters an error."""
    pass
