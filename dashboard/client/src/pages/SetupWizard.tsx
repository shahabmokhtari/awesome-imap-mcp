import { useState } from 'react'
import { useNavigate } from 'react-router-dom'
import {
  useSetupPin,
  useAuthStatus,
} from '../hooks/useApi'
import AddAccountForm from '../components/AddAccountForm'

// ---------------------------------------------------------------------------
// Step indicator
// ---------------------------------------------------------------------------

function StepIndicator({ current, total }: { current: number; total: number }) {
  return (
    <div className="flex items-center gap-2 mb-8">
      {Array.from({ length: total }, (_, i) => (
        <div key={i} className="flex items-center gap-2">
          <div
            className={`w-8 h-8 rounded-full flex items-center justify-center text-sm font-medium ${
              i < current
                ? 'bg-blue-600 text-white'
                : i === current
                  ? 'bg-blue-600 text-white ring-4 ring-blue-100'
                  : 'bg-gray-200 text-gray-500'
            }`}
          >
            {i < current ? '\u2713' : i + 1}
          </div>
          {i < total - 1 && (
            <div className={`w-12 h-0.5 ${i < current ? 'bg-blue-600' : 'bg-gray-200'}`} />
          )}
        </div>
      ))}
    </div>
  )
}

// ---------------------------------------------------------------------------
// Step 1: Set PIN
// ---------------------------------------------------------------------------

function StepPin({
  onNext,
  onSkip,
}: {
  onNext: () => void
  onSkip: () => void
}) {
  const [pin, setPin] = useState('')
  const [confirmPin, setConfirmPin] = useState('')
  const [error, setError] = useState<string | null>(null)
  const setupPin = useSetupPin()

  const handleSubmit = (e: React.FormEvent) => {
    e.preventDefault()
    setError(null)

    if (pin.length < 4 || pin.length > 6) {
      setError('PIN must be 4-6 digits.')
      return
    }
    if (!/^\d+$/.test(pin)) {
      setError('PIN must contain only digits.')
      return
    }
    if (pin !== confirmPin) {
      setError('PINs do not match.')
      return
    }

    setupPin.mutate(pin, {
      onSuccess: (data) => {
        if (data.token) {
          localStorage.setItem('dashboard_token', data.token)
        }
        onNext()
      },
      onError: (err) => setError(err.message),
    })
  }

  return (
    <div>
      <h2 className="text-2xl font-bold text-gray-900 mb-2">Set a Dashboard PIN</h2>
      <p className="text-gray-500 mb-6">
        Protect your dashboard with a PIN. You can skip this step if you don't need authentication.
      </p>

      <form onSubmit={handleSubmit} className="space-y-4 max-w-sm">
        <div>
          <label className="block text-sm font-medium text-gray-700 mb-1">PIN (4-6 digits)</label>
          <input
            type="password"
            inputMode="numeric"
            maxLength={6}
            value={pin}
            onChange={e => setPin(e.target.value)}
            placeholder="Enter PIN"
            className="w-full border border-gray-300 rounded-md px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
          />
        </div>
        <div>
          <label className="block text-sm font-medium text-gray-700 mb-1">Confirm PIN</label>
          <input
            type="password"
            inputMode="numeric"
            maxLength={6}
            value={confirmPin}
            onChange={e => setConfirmPin(e.target.value)}
            placeholder="Confirm PIN"
            className="w-full border border-gray-300 rounded-md px-3 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-blue-500"
          />
        </div>

        {error && (
          <div className="bg-red-50 border border-red-200 rounded-lg p-3 text-sm text-red-700">
            {error}
          </div>
        )}

        <div className="flex items-center gap-3 pt-2">
          <button
            type="submit"
            disabled={setupPin.isPending}
            className="px-5 py-2 bg-blue-600 text-white rounded-lg text-sm hover:bg-blue-700 transition-colors disabled:opacity-50"
          >
            {setupPin.isPending ? 'Setting up...' : 'Set PIN'}
          </button>
          <button
            type="button"
            onClick={onSkip}
            className="px-5 py-2 text-gray-600 bg-gray-100 rounded-lg text-sm hover:bg-gray-200 transition-colors"
          >
            Skip
          </button>
        </div>
      </form>
    </div>
  )
}

// ---------------------------------------------------------------------------
// Step 2: Add First Account (wraps shared AddAccountForm)
// ---------------------------------------------------------------------------

function StepAccount({ onNext, onSkip }: { onNext: () => void; onSkip: () => void }) {
  return (
    <div>
      <h2 className="text-2xl font-bold text-gray-900 mb-2">Add Your First Account</h2>
      <p className="text-gray-500 mb-6">Connect an email account, or skip this step.</p>
      <AddAccountForm
        onComplete={onNext}
        onCancel={onSkip}
      />
    </div>
  )
}

// ---------------------------------------------------------------------------
// Step 3: Done
// ---------------------------------------------------------------------------

function StepDone() {
  const navigate = useNavigate()

  const handleGo = () => {
    localStorage.setItem('setup_completed', 'true')
    navigate('/')
  }

  return (
    <div className="text-center py-8">
      <div className="w-16 h-16 bg-green-100 rounded-full flex items-center justify-center mx-auto mb-4">
        <svg className="w-8 h-8 text-green-600" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
          <path strokeLinecap="round" strokeLinejoin="round" d="M5 13l4 4L19 7" />
        </svg>
      </div>
      <h2 className="text-2xl font-bold text-gray-900 mb-2">You're all set!</h2>
      <p className="text-gray-500 mb-8">
        Your account has been configured. The server will start syncing your email shortly.
      </p>
      <button
        onClick={handleGo}
        className="px-6 py-2.5 bg-blue-600 text-white rounded-lg text-sm font-medium hover:bg-blue-700 transition-colors"
      >
        Go to Dashboard
      </button>
    </div>
  )
}

// ---------------------------------------------------------------------------
// Wizard container
// ---------------------------------------------------------------------------

export default function SetupWizard() {
  const authStatus = useAuthStatus()
  const showPinStep = authStatus.data ? !authStatus.data.hasPinSet : true

  const [step, setStep] = useState(0)

  // If PIN is already set, skip that step
  const steps = showPinStep
    ? ['pin', 'account', 'done'] as const
    : ['account', 'done'] as const

  const currentStepName = steps[step]
  const totalSteps = steps.length

  return (
    <div className="min-h-screen bg-gray-50 flex items-center justify-center p-4">
      <div className="w-full max-w-2xl">
        {/* Header */}
        <div className="text-center mb-8">
          <h1 className="text-3xl font-bold text-gray-900 mb-1">IMAP Dashboard Setup</h1>
          <p className="text-gray-500">Let's get your email connected in a few quick steps.</p>
        </div>

        {/* Step indicator */}
        <div className="flex justify-center">
          <StepIndicator current={step} total={totalSteps} />
        </div>

        {/* Step content card */}
        <div className="bg-white rounded-xl shadow-lg p-8">
          {currentStepName === 'pin' && (
            <StepPin
              onNext={() => setStep(s => s + 1)}
              onSkip={() => setStep(s => s + 1)}
            />
          )}
          {currentStepName === 'account' && (
            <StepAccount onNext={() => setStep(s => s + 1)} onSkip={() => setStep(s => s + 1)} />
          )}
          {currentStepName === 'done' && <StepDone />}
        </div>
      </div>
    </div>
  )
}
